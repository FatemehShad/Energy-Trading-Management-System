using System.Net;
using System.Net.Http.Json;
using EnergyTrading.Api.Domain;
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
    [PostgresFact]
    public async Task TradeLifecyclePersistsOutboxAndUpdatesPositions()
    {
        await using var app = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
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
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        await db.Database.MigrateAsync();
        var client = app.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/trades")).StatusCode);
        client.DefaultRequestHeaders.Add("X-Api-Key", "integration-test-key");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
        var suffix = Guid.NewGuid().ToString();
        var request = new CreateTrade(suffix, suffix, "POWER-DE", TradeSide.Buy, 10, -20,
            DateTimeOffset.Parse("2027-01-01T00:00:00Z"), DateTimeOffset.Parse("2027-01-01T01:00:00Z"));
        var invalid = await client.PostAsJsonAsync("/api/trades", request with { QuantityMwh = 0 });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var created = await client.PostAsJsonAsync("/api/trades", request);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/trades/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/trades", request)).StatusCode);
        var positions = await client.GetFromJsonAsync<List<Position>>($"/api/positions?portfolio={suffix}");
        Assert.Equal(new Position(suffix, "POWER-DE", 10, 200), Assert.Single(positions!));
        Assert.Equal(1, await db.Outbox.CountAsync(m => m.AggregateId == id));
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/trades/{id}/cancel", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/trades/{id}/cancel", null)).StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<List<Position>>($"/api/positions?portfolio={suffix}"))!);
        Assert.Equal(2, await db.Outbox.CountAsync(m => m.AggregateId == id));
        var quote = await client.PostAsJsonAsync("/api/market-data", new CreateQuote(suffix, -15, DateTimeOffset.UtcNow));
        Assert.Equal(HttpStatusCode.Created, quote.StatusCode);
        var latest = await client.GetFromJsonAsync<MarketQuote>($"/api/market-data/latest?product={suffix}");
        Assert.Equal(-15, latest!.PricePerMwh);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/trades/{Guid.NewGuid()}")).StatusCode);
    }
}
