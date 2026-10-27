namespace TenantFlow.Core;

public sealed class WorkItem
{
    public Guid Id { get; set; }
    public required string TenantId { get; set; }
    public required string Title { get; set; }
    public string? Description { get; set; }
    public WorkItemStatus Status { get; set; }
    public long Version { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public enum WorkItemStatus { Open, InProgress, Done }

public sealed class IdempotencyRecord
{
    public required string TenantId { get; set; }
    public required string Key { get; set; }
    public required string RequestHash { get; set; }
    public int ResponseStatus { get; set; }
    public required string ResponseBody { get; set; }
    public required string Location { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class OutboxMessage
{
    public Guid Id { get; set; }
    public required string TenantId { get; set; }
    public required string EventType { get; set; }
    public Guid AggregateId { get; set; }
    public required string Payload { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public string? LastError { get; set; }
}
