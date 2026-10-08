using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;

namespace EnergyTrading.Api.Infrastructure;

public sealed class OutboxPublisher(IServiceScopeFactory scopes, IConfiguration config,
    ILogger<OutboxPublisher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var producer = new ProducerBuilder<string, string>(new ProducerConfig
        {
            BootstrapServers = config["Kafka:BootstrapServers"] ?? "localhost:9092",
            EnableIdempotence = true,
            MessageTimeoutMs = 10000
        }).Build();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
                // PostgreSQL transaction lock keeps multiple API replicas from publishing concurrently.
                await using var transaction = await db.Database.BeginTransactionAsync(stoppingToken);
                await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(724109)", stoppingToken);
                // Only the earliest pending version of each trade is eligible, even if clocks move backwards.
                var batch = await db.Outbox.PendingInOrder().Take(100).ToListAsync(stoppingToken);
                foreach (var message in batch)
                {
                    await producer.ProduceAsync(config["Kafka:Topic"] ?? "energy.trades.v1",
                        new Message<string, string> { Key = message.AggregateId.ToString(), Value = message.Payload }, stoppingToken);
                    message.PublishedAt = DateTimeOffset.UtcNow;
                }
                await db.SaveChangesAsync(stoppingToken);
                await transaction.CommitAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "Outbox publish failed; pending events will be retried."); }
            try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
        // Consumers must deduplicate by eventId: a crash after Kafka acknowledgement can cause redelivery.
    }
}
