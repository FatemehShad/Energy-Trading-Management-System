using EnergyTrading.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace EnergyTrading.Api.Infrastructure;

public sealed class TradingDbContext(DbContextOptions<TradingDbContext> options) : DbContext(options)
{
    public DbSet<Trade> Trades => Set<Trade>();
    public DbSet<MarketQuote> MarketQuotes => Set<MarketQuote>();
    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();
    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Trade>().Property(t => t.Status).IsConcurrencyToken();
        model.Entity<Trade>().HasIndex(t => t.ClientTradeId).IsUnique();
        model.Entity<Trade>().Property(t => t.ClientTradeId).HasMaxLength(100);
        model.Entity<Trade>().Property(t => t.Portfolio).HasMaxLength(100);
        model.Entity<Trade>().Property(t => t.Product).HasMaxLength(100);
        model.Entity<Trade>().Property(t => t.QuantityMwh).HasPrecision(18, 6);
        model.Entity<Trade>().Property(t => t.PricePerMwh).HasPrecision(18, 6);
        model.Entity<Trade>().HasIndex(t => new { t.Portfolio, t.Product, t.Status });
        model.Entity<MarketQuote>().Property(q => q.Product).HasMaxLength(100);
        model.Entity<MarketQuote>().Property(q => q.PricePerMwh).HasPrecision(18, 6);
        model.Entity<MarketQuote>().HasIndex(q => new { q.Product, q.ObservedAt });
        model.Entity<OutboxMessage>().HasIndex(m => new { m.PublishedAt, m.CreatedAt });
    }
}
