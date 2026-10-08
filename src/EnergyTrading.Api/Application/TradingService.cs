using System.Text.Json;
using System.Text.Json.Serialization;
using EnergyTrading.Api.Domain;
using EnergyTrading.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace EnergyTrading.Api.Application;

public sealed class TradingService(TradingDbContext db)
{
    public static void Validate(CreateTrade input)
    {
        ValidateName(input.ClientTradeId, "ClientTradeId");
        ValidateName(input.Portfolio, "Portfolio");
        ValidateName(input.Product, "Product");
        ValidateInstrument(input.Commodity, input.Currency);
        if (input.Side is null || !Enum.IsDefined(input.Side.Value)) throw new ArgumentException("Side must be Buy or Sell.");
        if (input.QuantityMwh <= 0 || input.QuantityMwh > 1_000_000_000m || decimal.Round(input.QuantityMwh, 6) != input.QuantityMwh)
            throw new ArgumentException("QuantityMwh must be positive, at most 1 billion, with at most six decimals.");
        ValidatePrice(input.PricePerMwh);
        if (input.DeliveryStart == default || input.DeliveryEnd <= input.DeliveryStart)
            throw new ArgumentException("DeliveryEnd must be after a nonempty DeliveryStart.");
        if (input.DeliveryStart.Offset != TimeSpan.Zero || input.DeliveryEnd.Offset != TimeSpan.Zero)
            throw new ArgumentException("Delivery timestamps must use UTC.");
    }
    public static void ValidateName(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 100 || value != value.Trim())
            throw new ArgumentException($"{name} must contain 1–100 characters without surrounding whitespace.");
    }
    public static void ValidateInstrument(Commodity? commodity, string? currency)
    {
        if (commodity is null || commodity == Commodity.Unknown || !Enum.IsDefined(commodity.Value))
            throw new ArgumentException("Commodity must be Electricity or Gas.");
        if (currency is null || currency.Length != 3 || currency.Any(c => c < 'A' || c > 'Z'))
            throw new ArgumentException("Currency must be a three-letter uppercase currency code, e.g. EUR or USD.");
    }
    public static void ValidatePrice(decimal price)
    {
        // Negative electricity prices are valid.
        if ((price < -1_000_000_000m || price > 1_000_000_000m) || decimal.Round(price, 6) != price)
            throw new ArgumentException("PricePerMwh must be within +/-1 billion with at most six decimals.");
    }
    public async Task<Trade> CreateAsync(CreateTrade input, CancellationToken ct)
    {
        Validate(input);
        var trade = new Trade { ClientTradeId = input.ClientTradeId, Portfolio = input.Portfolio,
            Product = input.Product, Commodity = input.Commodity!.Value, Currency = input.Currency!, Side = input.Side!.Value, QuantityMwh = input.QuantityMwh,
            PricePerMwh = input.PricePerMwh, DeliveryStart = input.DeliveryStart,
            DeliveryEnd = input.DeliveryEnd };
        db.Trades.Add(trade);
        AddEvent("TradeCreated", trade);
        // One SaveChanges transaction atomically persists trade and event.
        await db.SaveChangesAsync(ct);
        return trade;
    }
    public async Task<Trade?> CancelAsync(Guid id, CancellationToken ct)
    {
        var trade = await db.Trades.SingleOrDefaultAsync(t => t.Id == id, ct);
        if (trade is null || trade.Status == TradeStatus.Cancelled) return trade;
        trade.Status = TradeStatus.Cancelled;
        AddEvent("TradeCancelled", trade);
        await db.SaveChangesAsync(ct);
        return trade;
    }
    private void AddEvent(string type, Trade trade)
    {
        var message = new OutboxMessage { AggregateId = trade.Id, AggregateVersion = trade.Status == TradeStatus.Active ? 1 : 2 };
        message.Payload = JsonSerializer.Serialize(new { eventId = message.Id, eventType = type,
            schemaVersion = 2, aggregateVersion = message.AggregateVersion, occurredAt = message.CreatedAt, trade }, EventJson);
        db.Outbox.Add(message);
    }
    private static readonly JsonSerializerOptions EventJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };
    public static IReadOnlyList<Position> CalculatePositions(IEnumerable<Trade> trades) => trades
        .Where(t => t.Status == TradeStatus.Active)
        .GroupBy(t => new { t.Portfolio, t.Product, t.Commodity, t.Currency, t.DeliveryStart, t.DeliveryEnd })
        .Select(g => new Position(g.Key.Portfolio, g.Key.Product,
            g.Sum(t => (t.Side == TradeSide.Buy ? 1 : -1) * t.QuantityMwh),
            g.Sum(t => (t.Side == TradeSide.Buy ? -1 : 1) * t.QuantityMwh * t.PricePerMwh),
            g.Key.Commodity, g.Key.Currency, g.Key.DeliveryStart, g.Key.DeliveryEnd))
        .OrderBy(p => p.Portfolio).ThenBy(p => p.Product).ThenBy(p => p.Commodity).ThenBy(p => p.Currency)
        .ThenBy(p => p.DeliveryStart).ThenBy(p => p.DeliveryEnd).ToList();
}
