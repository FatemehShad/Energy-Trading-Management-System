using System.Text.Json.Serialization;

namespace EnergyTrading.Api.Domain;

public enum Commodity { Unknown, Electricity, Gas }
public enum TradeSide { Buy, Sell }
public enum TradeStatus { Active, Cancelled }
public sealed class Trade
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ClientTradeId { get; set; } = "";
    public string Portfolio { get; set; } = "";
    public string Product { get; set; } = "";
    public Commodity Commodity { get; set; }
    public string Currency { get; set; } = "UNSPECIFIED";
    public TradeSide Side { get; set; }
    public decimal QuantityMwh { get; set; }
    public decimal PricePerMwh { get; set; }
    public DateTimeOffset DeliveryStart { get; set; }
    public DateTimeOffset DeliveryEnd { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public TradeStatus Status { get; set; }
}
public sealed class MarketQuote
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Product { get; set; } = "";
    public Commodity Commodity { get; set; }
    public string Currency { get; set; } = "UNSPECIFIED";
    public decimal PricePerMwh { get; set; }
    public DateTimeOffset ObservedAt { get; set; }
}
public sealed class OutboxMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AggregateId { get; set; }
    public int AggregateVersion { get; set; }
    public string Payload { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? PublishedAt { get; set; }
}
public sealed record CreateTrade(string ClientTradeId, string Portfolio, string Product,
    TradeSide? Side, decimal QuantityMwh, [property: JsonRequired] decimal PricePerMwh,
    DateTimeOffset DeliveryStart, DateTimeOffset DeliveryEnd, Commodity? Commodity, string? Currency);
public sealed record CreateQuote(string Product, [property: JsonRequired] decimal PricePerMwh, DateTimeOffset ObservedAt, Commodity? Commodity, string? Currency);
public sealed record Position(string Portfolio, string Product, decimal NetQuantityMwh, decimal NetCashFlow,
    Commodity Commodity = Commodity.Unknown, string Currency = "UNSPECIFIED",
    DateTimeOffset DeliveryStart = default, DateTimeOffset DeliveryEnd = default);
