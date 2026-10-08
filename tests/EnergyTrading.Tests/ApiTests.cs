using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Net.Http.Json;
using EnergyTrading.Api.Domain;
using EnergyTrading.Api.Application;
using EnergyTrading.Api.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EnergyTrading.Tests;
public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TEST_DATABASE")))
            Skip = "Set TEST_DATABASE to a dedicated PostgreSQL test database.";
    }
}
public class ApiTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { Converters = { new JsonStringEnumConverter() } };
    [PostgresFact]
    public async Task TradeLifecyclePersistsOutboxAndUpdatesPositions()
    {
        await using var app = CreateApp();
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        await db.Database.MigrateAsync();
        var client = app.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/trades")).StatusCode);
        client.DefaultRequestHeaders.Add("X-Api-Key", "integration-test-key");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
        var suffix = Guid.NewGuid().ToString();
        var request = new CreateTrade(suffix, suffix, "POWER-DE", TradeSide.Buy, 10, -20,
            DateTimeOffset.Parse("2027-01-01T00:00:00Z"), DateTimeOffset.Parse("2027-01-01T01:00:00Z"), Commodity.Electricity, "EUR");
        var invalid = await client.PostAsJsonAsync("/api/trades", request with { QuantityMwh = 0 });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var created = await client.PostAsJsonAsync("/api/trades", request);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/trades/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/trades", request)).StatusCode);
        var positions = await client.GetFromJsonAsync<List<Position>>($"/api/positions?portfolio={suffix}", Json);
        Assert.Equal(new Position(suffix, "POWER-DE", 10, 200, Commodity.Electricity, "EUR", request.DeliveryStart, request.DeliveryEnd), Assert.Single(positions!));
        Assert.Equal(1, await db.Outbox.CountAsync(m => m.AggregateId == id));
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/trades/{id}/cancel", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/trades/{id}/cancel", null)).StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<List<Position>>($"/api/positions?portfolio={suffix}", Json))!);
        Assert.Equal(2, await db.Outbox.CountAsync(m => m.AggregateId == id));
        var quote = await client.PostAsJsonAsync("/api/market-data", new CreateQuote(suffix, -15, DateTimeOffset.UtcNow, Commodity.Electricity, "EUR"));
        Assert.Equal(HttpStatusCode.Created, quote.StatusCode);
        var latest = await client.GetFromJsonAsync<MarketQuote>($"/api/market-data/latest?product={suffix}&commodity=Electricity&currency=EUR", Json);
        Assert.Equal(-15, latest!.PricePerMwh);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/trades/{Guid.NewGuid()}")).StatusCode);
    }
    private static WebApplicationFactory<Program> CreateApp()
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                foreach (var descriptor in services.Where(d => d.ImplementationType == typeof(OutboxPublisher)).ToList())
                    services.Remove(descriptor);
            }).ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Trading"] = Environment.GetEnvironmentVariable("TEST_DATABASE"),
                ["Kafka:Enabled"] = "false",
                ["ApiKey"] = "integration-test-key"
            })));
    }

    [PostgresFact]
    public async Task ApiSeparatesInstrumentsAndRejectsIncompleteTrades()
    {
        await using var app = CreateApp();
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        await db.Database.MigrateAsync();
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "integration-test-key");
        var portfolio = Guid.NewGuid().ToString();
        var start = DateTimeOffset.Parse("2027-01-01T00:00:00Z");
        CreateTrade Item(Commodity commodity, string currency, DateTimeOffset delivery) =>
            new(Guid.NewGuid().ToString(), portfolio, "ENERGY", TradeSide.Buy, 10, 50,
                delivery, delivery.AddHours(1), commodity, currency);
        foreach (var input in new[] { Item(Commodity.Electricity, "EUR", start), Item(Commodity.Gas, "EUR", start),
            Item(Commodity.Electricity, "USD", start), Item(Commodity.Electricity, "EUR", start.AddDays(1)) })
            Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/trades", input)).StatusCode);
        var positions = await client.GetFromJsonAsync<List<Position>>($"/api/positions?portfolio={portfolio}", Json);
        Assert.Equal(4, positions!.Count);
        Assert.All(positions, p => Assert.Equal(10, p.NetQuantityMwh));
        var valid = Item(Commodity.Gas, "EUR", start);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/trades", valid with { Commodity = null })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/trades", valid with { Side = null })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/trades", valid with { Currency = null })).StatusCode);
        var missingPrice = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(valid, Json))!;
        missingPrice.AsObject().Remove("pricePerMwh");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/trades", missingPrice)).StatusCode);
        Assert.Equal(4, await db.Trades.CountAsync(t => t.Portfolio == portfolio));
        foreach (var instrument in new[] { (Commodity.Gas, "EUR", 20m), (Commodity.Electricity, "EUR", 40m), (Commodity.Gas, "USD", 60m) })
            Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/market-data",
                new CreateQuote(portfolio, instrument.Item3, DateTimeOffset.UtcNow, instrument.Item1, instrument.Item2))).StatusCode);
        var quote = await client.GetFromJsonAsync<MarketQuote>($"/api/market-data/latest?product={portfolio}&commodity=Gas&currency=EUR", Json);
        Assert.Equal(20, quote!.PricePerMwh);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/market-data/latest?product={portfolio}")).StatusCode);
    }

    [PostgresFact]
    public async Task CancellationRaceRollsBackAndOutboxRespectsVersions()
    {
        await using var app = CreateApp();
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        await db.Database.MigrateAsync();
        var options = new DbContextOptionsBuilder<TradingDbContext>().UseNpgsql(Environment.GetEnvironmentVariable("TEST_DATABASE")).Options;
        await using var first = new TradingDbContext(options);
        await using var second = new TradingDbContext(options);
        var start = DateTimeOffset.Parse("2027-01-01T00:00:00Z");
        var input = new CreateTrade(Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), "GAS", TradeSide.Sell,
            10, 50, start, start.AddHours(1), Commodity.Gas, "EUR");
        var service = new TradingService(first);
        var trade = await service.CreateAsync(input, default);
        await second.Trades.SingleAsync(t => t.Id == trade.Id); // Both contexts retain the original Active version.
        await service.CancelAsync(trade.Id, default);
        await Assert.ThrowsAnyAsync<DbUpdateException>(() => new TradingService(second).CancelAsync(trade.Id, default));
        Assert.Equal(2, await db.Outbox.CountAsync(m => m.AggregateId == trade.Id));
        var messages = await db.Outbox.Where(m => m.AggregateId == trade.Id).OrderBy(m => m.AggregateVersion).ToListAsync();
        Assert.Equal(new[] { 1, 2 }, messages.Select(m => m.AggregateVersion));
        var payload = JsonDocument.Parse(messages[0].Payload).RootElement;
        Assert.Equal(2, payload.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("Gas", payload.GetProperty("trade").GetProperty("commodity").GetString());
        Assert.Equal("Sell", payload.GetProperty("trade").GetProperty("side").GetString());
        // Force clock reversal: cancellation still cannot overtake creation.
        messages[0].CreatedAt = DateTimeOffset.UtcNow.AddDays(1);
        messages[1].CreatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        Assert.Equal(1, (await db.Outbox.PendingInOrder().SingleAsync(m => m.AggregateId == trade.Id)).AggregateVersion);
        messages[0].PublishedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        Assert.Equal(2, (await db.Outbox.PendingInOrder().SingleAsync(m => m.AggregateId == trade.Id)).AggregateVersion);
        // Duplicate booking must roll back its outbox insert as well.
        await using var duplicate = new TradingDbContext(options);
        var before = await db.Outbox.CountAsync();
        await Assert.ThrowsAsync<DbUpdateException>(() => new TradingService(duplicate).CreateAsync(input, default));
        Assert.Equal(before, await db.Outbox.CountAsync());
    }

}
