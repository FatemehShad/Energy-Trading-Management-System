using EnergyTrading.Api.Application;
using EnergyTrading.Api.Domain;
using Xunit;

namespace EnergyTrading.Tests;
public class TradingTests
{
    private static CreateTrade Valid() => new("client-1", "portfolio-1", "POWER-DE", TradeSide.Buy,
        10m, -20m, DateTimeOffset.Parse("2027-01-01T00:00:00Z"), DateTimeOffset.Parse("2027-01-01T01:00:00Z"));
    [Fact] public void NegativeElectricityPricesAreValid() => TradingService.Validate(Valid());
    [Theory] [InlineData(0)] [InlineData(-1)]
    public void RejectsNonpositiveQuantity(decimal quantity) => Assert.Throws<ArgumentException>(() => TradingService.Validate(Valid() with { QuantityMwh = quantity }));
    [Fact] public void RejectsUnknownSide() => Assert.Throws<ArgumentException>(() => TradingService.Validate(Valid() with { Side = (TradeSide)42 }));
    [Fact] public void RejectsReversedDelivery() => Assert.Throws<ArgumentException>(() => TradingService.Validate(Valid() with { DeliveryEnd = Valid().DeliveryStart }));
    [Fact] public void RejectsExcessPrecision() => Assert.Throws<ArgumentException>(() => TradingService.Validate(Valid() with { QuantityMwh = 1.0000001m }));
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
