using EnergyTrading.Api.Application;
using EnergyTrading.Api.Domain;
using Xunit;

namespace EnergyTrading.Tests;
public class TradingTests
{
    private static CreateTrade Valid() => new("client-1", "portfolio-1", "POWER-DE", TradeSide.Buy,
        10m, -20m, DateTimeOffset.Parse("2027-01-01T00:00:00Z"), DateTimeOffset.Parse("2027-01-01T01:00:00Z"), Commodity.Electricity, "EUR");
    [Fact] public void NegativeElectricityPricesAreValid() => TradingService.Validate(Valid());
    [Theory] [InlineData(0)] [InlineData(-1)]
    public void RejectsNonpositiveQuantity(decimal quantity) => Assert.Throws<ArgumentException>(() => TradingService.Validate(Valid() with { QuantityMwh = quantity }));
    [Fact] public void RejectsUnknownSide() => Assert.Throws<ArgumentException>(() => TradingService.Validate(Valid() with { Side = (TradeSide)42 }));
    [Fact] public void RejectsReversedDelivery() => Assert.Throws<ArgumentException>(() => TradingService.Validate(Valid() with { DeliveryEnd = Valid().DeliveryStart }));
    [Fact] public void RejectsExcessPrecision() => Assert.Throws<ArgumentException>(() => TradingService.Validate(Valid() with { QuantityMwh = 1.0000001m }));
    [Theory] [InlineData(null)] [InlineData(Commodity.Unknown)] [InlineData((Commodity)99)]
    public void RejectsMissingOrInvalidCommodity(Commodity? commodity) =>
        Assert.Throws<ArgumentException>(() => TradingService.Validate(Valid() with { Commodity = commodity }));
    [Theory] [InlineData(null)] [InlineData("eur")] [InlineData("EU")] [InlineData(" EUR ")]
    public void RejectsInvalidCurrency(string? currency) =>
        Assert.Throws<ArgumentException>(() => TradingService.Validate(Valid() with { Currency = currency }));
    [Fact] public void RejectsMissingSide() => Assert.Throws<ArgumentException>(() => TradingService.Validate(Valid() with { Side = null }));
    [Fact] public void RejectsNonUtcDelivery() => Assert.Throws<ArgumentException>(() => TradingService.Validate(Valid() with
    { DeliveryStart = Valid().DeliveryStart.ToOffset(TimeSpan.FromHours(1)) }));
    [Fact] public void PositionsSeparateCommodityCurrencyAndDelivery()
    {
        var start = Valid().DeliveryStart;
        Trade Item(Commodity commodity, string currency, DateTimeOffset delivery) => new()
        { Portfolio = "P", Product = "ENERGY", Commodity = commodity, Currency = currency,
          DeliveryStart = delivery, DeliveryEnd = delivery.AddHours(1), QuantityMwh = 10, PricePerMwh = 50 };
        var positions = TradingService.CalculatePositions(new[]
        {
            Item(Commodity.Electricity, "EUR", start), Item(Commodity.Gas, "EUR", start),
            Item(Commodity.Electricity, "USD", start), Item(Commodity.Electricity, "EUR", start.AddDays(1))
        });
        Assert.Equal(4, positions.Count);
        Assert.All(positions, p => Assert.Equal(10, p.NetQuantityMwh));
    }
    [Fact] public void PositionsNetBuysAndSellsAndExcludeCancelledTrades()
    {
        var trades = new[] {
            new Trade { Portfolio = "P", Product = "POWER", Side = TradeSide.Buy, QuantityMwh = 10, PricePerMwh = 50 },
            new Trade { Portfolio = "P", Product = "POWER", Side = TradeSide.Sell, QuantityMwh = 4, PricePerMwh = 60 },
            new Trade { Portfolio = "P", Product = "POWER", Side = TradeSide.Buy, QuantityMwh = 100, Status = TradeStatus.Cancelled },
            new Trade { Portfolio = "P2", Product = "GAS", Side = TradeSide.Buy, QuantityMwh = 2, PricePerMwh = -5 }
        };
        var positions = TradingService.CalculatePositions(trades);
        Assert.Equal(2, positions.Count);
        Assert.Equal(new Position("P", "POWER", 6, -260), positions[0]);
        Assert.Equal(new Position("P2", "GAS", 2, 10), positions[1]);
    }
}
