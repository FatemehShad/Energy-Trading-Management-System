using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.Text;
using EnergyTrading.Api.Application;
using EnergyTrading.Api.Domain;
using EnergyTrading.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDbContext<TradingDbContext>(o => o.UseNpgsql(
    builder.Configuration.GetConnectionString("Trading") ?? throw new InvalidOperationException("ConnectionStrings:Trading is required.")));
builder.Services.AddScoped<TradingService>();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
if (builder.Configuration.GetValue("Kafka:Enabled", true)) builder.Services.AddHostedService<OutboxPublisher>();
var app = builder.Build();
var apiKey = builder.Configuration["ApiKey"];
if (!app.Environment.IsDevelopment() && string.IsNullOrWhiteSpace(apiKey) && !args.Contains("--migrate"))
    throw new InvalidOperationException("ApiKey is required outside Development.");
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api") && !string.IsNullOrWhiteSpace(apiKey))
    {
        var supplied = context.Request.Headers["X-Api-Key"].ToString();
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(supplied)),
            SHA256.HashData(Encoding.UTF8.GetBytes(apiKey))))
        { context.Response.StatusCode = 401; return; }
    }
    await next(context);
});
if (args.Contains("--migrate"))
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<TradingDbContext>().Database.MigrateAsync();
    return;
}
if (app.Environment.IsDevelopment()) { app.UseSwagger(); app.UseSwaggerUI(); }
app.Use(async (context, next) =>
{
    try { await next(context); }
    catch (ArgumentException ex) { await Results.Problem(ex.Message, statusCode: 400).ExecuteAsync(context); }
    catch (DbUpdateConcurrencyException)
    { await Results.Problem("Trade changed concurrently; retry the request.", statusCode: 409).ExecuteAsync(context); }
    catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
    { await Results.Problem("ClientTradeId already exists.", statusCode: 409).ExecuteAsync(context); }
});
app.MapGet("/health/live", () => Results.Ok(new { status = "alive" }));
app.MapGet("/health/ready", async (TradingDbContext db, CancellationToken ct) =>
{
    try { await db.Trades.Take(1).ToListAsync(ct); return Results.Ok(new { status = "ready" }); }
    catch (Exception) { return Results.StatusCode(503); }
});
app.MapPost("/api/trades", async (CreateTrade request, TradingService service, CancellationToken ct) =>
{
    var trade = await service.CreateAsync(request, ct);
    return Results.Created($"/api/trades/{trade.Id}", trade);
});
app.MapGet("/api/trades/{id:guid}", async (Guid id, TradingDbContext db, CancellationToken ct) =>
    await db.Trades.AsNoTracking().SingleOrDefaultAsync(t => t.Id == id, ct) is { } trade ? Results.Ok(trade) : Results.NotFound());
app.MapGet("/api/trades", async (TradingDbContext db, string? portfolio, int? page, int? pageSize, CancellationToken ct) =>
{
    var number = page ?? 1;
    var size = pageSize ?? 50;
    if (number < 1 || number > 1_000_000 || size < 1 || size > 200) return Results.BadRequest("Invalid pagination.");
    var query = db.Trades.AsNoTracking();
    if (portfolio is not null) query = query.Where(t => t.Portfolio == portfolio);
    return Results.Ok(await query.OrderByDescending(t => t.CreatedAt).ThenBy(t => t.Id)
        .Skip((number - 1) * size).Take(size).ToListAsync(ct));
});
app.MapPost("/api/trades/{id:guid}/cancel", async (Guid id, TradingService service, CancellationToken ct) =>
    await service.CancelAsync(id, ct) is { } trade ? Results.Ok(trade) : Results.NotFound());
app.MapGet("/api/positions", async (TradingDbContext db, string? portfolio, CancellationToken ct) =>
{
    var query = db.Trades.AsNoTracking().Where(t => t.Status == TradeStatus.Active);
    if (portfolio is not null) query = query.Where(t => t.Portfolio == portfolio);
    // Aggregate in PostgreSQL rather than loading the entire trade book.
    return Results.Ok(await query.GroupBy(t => new { t.Portfolio, t.Product })
        .OrderBy(g => g.Key.Portfolio).ThenBy(g => g.Key.Product)
        .Select(g => new Position(g.Key.Portfolio, g.Key.Product,
            g.Sum(t => (t.Side == TradeSide.Buy ? 1 : -1) * t.QuantityMwh),
            g.Sum(t => (t.Side == TradeSide.Buy ? -1 : 1) * t.QuantityMwh * t.PricePerMwh)))
        .ToListAsync(ct));
});
app.MapPost("/api/market-data", async (CreateQuote request, TradingDbContext db, CancellationToken ct) =>
{
    TradingService.ValidateName(request.Product, "Product");
    TradingService.ValidatePrice(request.PricePerMwh);
    if (request.ObservedAt == default || request.ObservedAt.Offset != TimeSpan.Zero)
        throw new ArgumentException("ObservedAt must be a nonempty UTC timestamp.");
    var quote = new MarketQuote { Product = request.Product, PricePerMwh = request.PricePerMwh, ObservedAt = request.ObservedAt };
    db.MarketQuotes.Add(quote);
    await db.SaveChangesAsync(ct);
    return Results.Created($"/api/market-data/{quote.Id}", quote);
});
app.MapGet("/api/market-data/{id:guid}", async (Guid id, TradingDbContext db, CancellationToken ct) =>
    await db.MarketQuotes.AsNoTracking().SingleOrDefaultAsync(q => q.Id == id, ct) is { } quote ? Results.Ok(quote) : Results.NotFound());
app.MapGet("/api/market-data/latest", async (string product, TradingDbContext db, CancellationToken ct) =>
    await db.MarketQuotes.AsNoTracking().Where(q => q.Product == product).OrderByDescending(q => q.ObservedAt)
        .ThenBy(q => q.Id).FirstOrDefaultAsync(ct) is { } quote ? Results.Ok(quote) : Results.NotFound());
await app.RunAsync();
public partial class Program { }
