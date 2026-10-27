using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace TenantFlow.Core;

public sealed record CreateWorkItem(string Title, string? Description);
public sealed record UpdateWorkItem(string Title, string? Description, WorkItemStatus Status);
public sealed record WorkItemDto(Guid Id, string Title, string? Description, WorkItemStatus Status, long Version, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record IdempotentResult(int StatusCode, string Body, string Location, bool Replayed);

public sealed class IdempotencyConflictException : Exception
{
    public IdempotencyConflictException() : base("The idempotency key was already used with a different payload.") { }
}

public sealed class StaleVersionException : Exception
{
    public StaleVersionException() : base("The resource changed after the supplied ETag was issued.") { }
}

public sealed class IdempotencyCoordinator
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    public async ValueTask<IAsyncDisposable> AcquireAsync(string tenantId, string key, CancellationToken cancellationToken)
    {
        var gate = _locks.GetOrAdd($"{tenantId}\n{key}", static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        return new Releaser(gate);
    }

    private sealed class Releaser(SemaphoreSlim gate) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            gate.Release();
            return ValueTask.CompletedTask;
        }
    }
}

public sealed class WorkItemService(
    TenantFlowDbContext db,
    ITenantContext tenantContext,
    IdempotencyCoordinator coordinator,
    TimeProvider timeProvider)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<WorkItemDto>> ListAsync(CancellationToken cancellationToken) =>
        await db.WorkItems.AsNoTracking()
            .Where(x => x.TenantId == tenantContext.RequireTenantId())
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => ToDto(x))
            .ToListAsync(cancellationToken);

    public async Task<WorkItemDto?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var item = await db.WorkItems.AsNoTracking().SingleOrDefaultAsync(
            x => x.TenantId == tenantContext.RequireTenantId() && x.Id == id,
            cancellationToken);
        return item is null ? null : ToDto(item);
    }

    public async Task<IdempotentResult> CreateAsync(
        CreateWorkItem request,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var tenantId = tenantContext.RequireTenantId();
        var normalized = Normalize(request);
        Validate(normalized.Title);
        var requestHash = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(normalized, JsonOptions))));

        await using var lease = await coordinator.AcquireAsync(tenantId, idempotencyKey, cancellationToken);
        var existing = await db.IdempotencyRecords.AsNoTracking().SingleOrDefaultAsync(
            x => x.TenantId == tenantId && x.Key == idempotencyKey,
            cancellationToken);
        if (existing is not null)
        {
            return Replay(existing, requestHash);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        existing = await db.IdempotencyRecords.AsNoTracking().SingleOrDefaultAsync(
            x => x.TenantId == tenantId && x.Key == idempotencyKey,
            cancellationToken);
        if (existing is not null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Replay(existing, requestHash);
        }

        var now = timeProvider.GetUtcNow();
        var item = new WorkItem
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Title = normalized.Title,
            Description = normalized.Description,
            Status = WorkItemStatus.Open,
            CreatedAt = now,
            UpdatedAt = now
        };
        var dto = ToDto(item);
        var body = JsonSerializer.Serialize(dto, JsonOptions);
        var location = $"/api/work-items/{item.Id}";

        db.WorkItems.Add(item);
        db.OutboxMessages.Add(NewOutbox(item, "work-item.created", dto, now));
        db.IdempotencyRecords.Add(new IdempotencyRecord
        {
            TenantId = tenantId,
            Key = idempotencyKey,
            RequestHash = requestHash,
            ResponseStatus = 201,
            ResponseBody = body,
            Location = location,
            CreatedAt = now
        });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new IdempotentResult(201, body, location, false);
    }

    public async Task<WorkItemDto?> UpdateAsync(
        Guid id,
        UpdateWorkItem request,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        var title = request.Title.Trim();
        Validate(title);
        var tenantId = tenantContext.RequireTenantId();
        var item = await db.WorkItems.SingleOrDefaultAsync(
            x => x.TenantId == tenantId && x.Id == id,
            cancellationToken);
        if (item is null) return null;
        if (item.Version != expectedVersion) throw new StaleVersionException();

        db.Entry(item).Property(x => x.Version).OriginalValue = expectedVersion;
        item.Title = title;
        item.Description = NormalizeOptional(request.Description);
        item.Status = request.Status;
        item.Version++;
        item.UpdatedAt = timeProvider.GetUtcNow();
        var dto = ToDto(item);
        db.OutboxMessages.Add(NewOutbox(item, "work-item.updated", dto, item.UpdatedAt));

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new StaleVersionException();
        }

        return dto;
    }

    private static IdempotentResult Replay(IdempotencyRecord existing, string requestHash)
    {
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(existing.RequestHash),
                Convert.FromHexString(requestHash)))
        {
            throw new IdempotencyConflictException();
        }

        return new IdempotentResult(existing.ResponseStatus, existing.ResponseBody, existing.Location, true);
    }

    private static CreateWorkItem Normalize(CreateWorkItem request) =>
        new(request.Title?.Trim() ?? string.Empty, NormalizeOptional(request.Description));

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void Validate(string title)
    {
        if (title.Length is < 1 or > 200)
            throw new ArgumentException("Title must contain 1 to 200 characters.");
    }

    private static WorkItemDto ToDto(WorkItem item) =>
        new(item.Id, item.Title, item.Description, item.Status, item.Version, item.CreatedAt, item.UpdatedAt);

    private static OutboxMessage NewOutbox(WorkItem item, string eventType, WorkItemDto payload, DateTimeOffset now) =>
        new()
        {
            Id = Guid.NewGuid(),
            TenantId = item.TenantId,
            EventType = eventType,
            AggregateId = item.Id,
            Payload = JsonSerializer.Serialize(payload, JsonOptions),
            OccurredAt = now,
            NextAttemptAt = now
        };
}
