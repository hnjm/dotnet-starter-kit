using FSH.Framework.Eventing.Abstractions;

namespace FSH.Framework.Eventing.Outbox;

/// <summary>
/// Abstraction for persisting and reading outbox messages.
/// </summary>
public interface IOutboxStore
{
    Task AddAsync(IIntegrationEvent @event, CancellationToken ct = default);

    Task<IReadOnlyList<OutboxMessage>> GetPendingBatchAsync(int batchSize, CancellationToken ct = default);

    Task MarkAsProcessedAsync(OutboxMessage message, CancellationToken ct = default);

    Task MarkAsFailedAsync(OutboxMessage message, string error, bool isDead, CancellationToken ct = default);

    /// <summary>Lists dead-lettered (exhausted) messages so an operator can inspect them.</summary>
    Task<IReadOnlyList<OutboxMessage>> GetDeadLetteredAsync(int max, CancellationToken ct = default);

    /// <summary>
    /// Resets dead-lettered messages for another dispatch attempt (clears the dead flag, retry
    /// count, last error and backoff). Pass specific ids, or <c>null</c> to redrive them all.
    /// Returns the number of messages redriven.
    /// </summary>
    Task<int> RedriveDeadLettersAsync(IReadOnlyCollection<Guid>? ids, CancellationToken ct = default);
}