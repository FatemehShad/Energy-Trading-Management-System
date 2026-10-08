using EnergyTrading.Api.Domain;

namespace EnergyTrading.Api.Infrastructure;

public static class OutboxQueries
{
    public static IQueryable<OutboxMessage> PendingInOrder(this IQueryable<OutboxMessage> messages) =>
        messages.Where(m => m.PublishedAt == null && !messages.Any(earlier =>
            earlier.AggregateId == m.AggregateId && earlier.AggregateVersion < m.AggregateVersion && earlier.PublishedAt == null))
            .OrderBy(m => m.CreatedAt).ThenBy(m => m.Id);
}
