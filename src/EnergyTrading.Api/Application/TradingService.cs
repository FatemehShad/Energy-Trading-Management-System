using System.Text.Json;
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
        if (!Enum.IsDefined(input.Side)) throw new ArgumentException("Side must be Buy or Sell.");
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
            Product = input.Product, Side = input.Side, QuantityMwh = input.QuantityMwh,
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
        var message = new OutboxMessage { AggregateId = trade.Id };
        message.Payload = JsonSerializer.Serialize(new { eventId = message.Id, eventType = type,
            schemaVersion = 1, occurredAt = message.CreatedAt, trade });
        db.Outbox.Add(message);
    }
    public static IReadOnlyList<Position> CalculatePositions(IEnumerable<Trade> trades) => trades
        .Where(t => t.Status == TradeStatus.Active)
        .GroupBy(t => new { t.Portfolio, t.Product })
        .Select(g => new Position(g.Key.Portfolio, g.Key.Product,
            g.Sum(t => (t.Side == TradeSide.Buy ? 1 : -1) * t.QuantityMwh),
            g.Sum(t => (t.Side == TradeSide.Buy ? -1 : 1) * t.QuantityMwh * t.PricePerMwh)))
        .OrderBy(p => p.Portfolio).ThenBy(p => p.Product).ToList();
}
