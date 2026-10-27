using Azure.Messaging.ServiceBus;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace TenantFlow.Core;

public interface IEventPublisher
{
    Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken);
}

public sealed class LocalEventPublisher(ILogger<LocalEventPublisher> logger) : IEventPublisher
{
    public Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Local event {EventId} {EventType} for tenant {TenantId}: {Payload}",
            message.Id, message.EventType, message.TenantId, message.Payload);
        return Task.CompletedTask;
    }
}

public sealed class AzureServiceBusEventPublisher : IEventPublisher, IAsyncDisposable
{
    private readonly ServiceBusClient _client;
    private readonly ServiceBusSender _sender;

    public AzureServiceBusEventPublisher(string connectionString, string topic)
    {
        _client = new ServiceBusClient(connectionString);
        _sender = _client.CreateSender(topic);
    }

    public Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        var serviceBusMessage = new ServiceBusMessage(message.Payload)
        {
            MessageId = message.Id.ToString("N"),
            CorrelationId = message.AggregateId.ToString("N"),
            Subject = message.EventType,
            ContentType = "application/json"
        };
        serviceBusMessage.ApplicationProperties["tenantId"] = message.TenantId;
        return _sender.SendMessageAsync(serviceBusMessage, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _sender.DisposeAsync();
        await _client.DisposeAsync();
    }
}

public sealed record DispatchSummary(int Published, int Failed);

public sealed class OutboxDispatchCoordinator
{
    internal SemaphoreSlim Gate { get; } = new(1, 1);
}

public sealed class OutboxDispatcher(
    TenantFlowDbContext db,
    IEventPublisher publisher,
    TimeProvider timeProvider,
    OutboxDispatchCoordinator coordinator,
    ILogger<OutboxDispatcher> logger)
{
    public async Task<DispatchSummary> DispatchAsync(int maximum, CancellationToken cancellationToken)
    {
        await coordinator.Gate.WaitAsync(cancellationToken);
        try
        {
            return await DispatchCoreAsync(maximum, cancellationToken);
        }
        finally
        {
            coordinator.Gate.Release();
        }
    }

    private async Task<DispatchSummary> DispatchCoreAsync(int maximum, CancellationToken cancellationToken)
    {
        maximum = Math.Clamp(maximum, 1, 100);
        var now = timeProvider.GetUtcNow();
        var messages = await db.OutboxMessages
            .Where(x => x.ProcessedAt == null && x.NextAttemptAt <= now)
            .OrderBy(x => x.OccurredAt)
            .Take(maximum)
            .ToListAsync(cancellationToken);
        var published = 0;
        var failed = 0;

        foreach (var message in messages)
        {
            try
            {
                await publisher.PublishAsync(message, cancellationToken);
                message.ProcessedAt = timeProvider.GetUtcNow();
                message.LastError = null;
                published++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                message.Attempts++;
                message.LastError = ex.Message.Length <= 500 ? ex.Message : ex.Message[..500];
                var delaySeconds = Math.Min(300, Math.Pow(2, Math.Min(message.Attempts, 8)));
                message.NextAttemptAt = timeProvider.GetUtcNow().AddSeconds(delaySeconds);
                logger.LogWarning(ex, "Outbox delivery {EventId} failed on attempt {Attempt}", message.Id, message.Attempts);
                failed++;
            }

            await db.SaveChangesAsync(cancellationToken);
        }

        return new DispatchSummary(published, failed);
    }
}
