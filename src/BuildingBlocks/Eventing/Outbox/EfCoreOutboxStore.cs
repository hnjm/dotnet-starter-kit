using FSH.Framework.Eventing.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FSH.Framework.Eventing.Outbox;

/// <summary>
/// EF Core-based outbox store for a specific DbContext.
/// </summary>
/// <typeparam name="TDbContext">The DbContext that owns the OutboxMessages set.</typeparam>
public sealed class EfCoreOutboxStore<TDbContext> : IOutboxStore
    where TDbContext : DbContext
{
    private readonly TDbContext _dbContext;
    private readonly IEventSerializer _serializer;
    private readonly ILogger<EfCoreOutboxStore<TDbContext>> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly EventingOptions _options;

    public EfCoreOutboxStore(
        TDbContext dbContext,
        IEventSerializer serializer,
        ILogger<EfCoreOutboxStore<TDbContext>> logger,
        TimeProvider timeProvider,
        IOptions<EventingOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _dbContext = dbContext;
        _serializer = serializer;
        _logger = logger;
        _timeProvider = timeProvider;
        _options = options.Value;
    }

    public async Task AddAsync(IIntegrationEvent @event, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(@event);

        var payload = _serializer.Serialize(@event);
        var message = new OutboxMessage
        {
            Id = @event.Id,
            CreatedOnUtc = @event.OccurredOnUtc,
            Type = @event.GetType().AssemblyQualifiedName ?? @event.GetType().FullName!,
            Payload = payload,
            TenantId = @event.TenantId,
            CorrelationId = @event.CorrelationId,
            RetryCount = 0,
            IsDead = false
        };

        await _dbContext.Set<OutboxMessage>().AddAsync(message, ct).ConfigureAwait(false);
        await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<OutboxMessage>> GetPendingBatchAsync(int batchSize, CancellationToken ct = default)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        return await _dbContext.Set<OutboxMessage>()
            .Where(m => !m.IsDead && m.ProcessedOnUtc == null && (m.NextRetryAt == null || m.NextRetryAt <= now))
            .OrderBy(m => m.CreatedOnUtc)
            .Take(batchSize)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task MarkAsProcessedAsync(OutboxMessage message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        message.ProcessedOnUtc = _timeProvider.GetUtcNow().UtcDateTime;
        _dbContext.Set<OutboxMessage>().Update(message);
        await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task MarkAsFailedAsync(OutboxMessage message, string error, bool isDead, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        message.RetryCount++;
        message.LastError = error;
        message.IsDead = isDead;
        // Space out retries with exponential backoff so a persistently failing message doesn't
        // re-fire every dispatch cycle. A dead message won't be retried, so leave it eligible-null.
        message.NextRetryAt = isDead ? null : _timeProvider.GetUtcNow().UtcDateTime.Add(BackoffFor(message.RetryCount));
        _dbContext.Set<OutboxMessage>().Update(message);

        await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<OutboxMessage>> GetDeadLetteredAsync(int max, CancellationToken ct = default)
    {
        if (max <= 0) max = 100;
        return await _dbContext.Set<OutboxMessage>()
            .Where(m => m.IsDead)
            .OrderBy(m => m.CreatedOnUtc)
            .Take(max)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<int> RedriveDeadLettersAsync(IReadOnlyCollection<Guid>? ids, CancellationToken ct = default)
    {
        var query = _dbContext.Set<OutboxMessage>().Where(m => m.IsDead);
        if (ids is { Count: > 0 })
        {
            query = query.Where(m => ids.Contains(m.Id));
        }

        var dead = await query.ToListAsync(ct).ConfigureAwait(false);
        foreach (var message in dead)
        {
            message.IsDead = false;
            message.RetryCount = 0;
            message.LastError = null;
            message.NextRetryAt = null;
        }

        if (dead.Count > 0)
        {
            await _dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
            Telemetry.EventingTelemetry.OutboxRedriven.Add(dead.Count);
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Redrove {Count} dead-lettered outbox message(s) for another attempt.", dead.Count);
            }
        }

        return dead.Count;
    }

    private TimeSpan BackoffFor(int retryCount)
    {
        var baseSeconds = _options.OutboxRetryBaseDelaySeconds > 0 ? _options.OutboxRetryBaseDelaySeconds : 30;
        var maxSeconds = _options.OutboxRetryMaxDelaySeconds > 0 ? _options.OutboxRetryMaxDelaySeconds : 3600;
        // retryCount is 1 after the first failure → first backoff is exactly baseSeconds.
        var exponent = Math.Min(retryCount - 1, 30); // clamp so the shift can't overflow
        var seconds = Math.Min((double)baseSeconds * Math.Pow(2, exponent), maxSeconds);
        return TimeSpan.FromSeconds(seconds);
    }
}