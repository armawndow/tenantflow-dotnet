using Microsoft.EntityFrameworkCore;

namespace TenantFlow.Core;

public sealed class TenantFlowDbContext(DbContextOptions<TenantFlowDbContext> options) : DbContext(options)
{
    public DbSet<WorkItem> WorkItems => Set<WorkItem>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<WorkItem>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.TenantId).HasMaxLength(80);
            entity.Property(x => x.Title).HasMaxLength(200);
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.Property(x => x.CreatedAt).HasConversion(
                value => value.ToUnixTimeMilliseconds(),
                value => DateTimeOffset.FromUnixTimeMilliseconds(value));
            entity.Property(x => x.UpdatedAt).HasConversion(
                value => value.ToUnixTimeMilliseconds(),
                value => DateTimeOffset.FromUnixTimeMilliseconds(value));
            entity.HasIndex(x => new { x.TenantId, x.Id }).IsUnique();
        });

        modelBuilder.Entity<IdempotencyRecord>(entity =>
        {
            entity.HasKey(x => new { x.TenantId, x.Key });
            entity.Property(x => x.TenantId).HasMaxLength(80);
            entity.Property(x => x.Key).HasMaxLength(100);
            entity.Property(x => x.RequestHash).HasMaxLength(64);
            entity.Property(x => x.Location).HasMaxLength(300);
            entity.Property(x => x.CreatedAt).HasConversion(
                value => value.ToUnixTimeMilliseconds(),
                value => DateTimeOffset.FromUnixTimeMilliseconds(value));
        });

        modelBuilder.Entity<OutboxMessage>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.TenantId).HasMaxLength(80);
            entity.Property(x => x.EventType).HasMaxLength(120);
            entity.Property(x => x.OccurredAt).HasConversion(
                value => value.ToUnixTimeMilliseconds(),
                value => DateTimeOffset.FromUnixTimeMilliseconds(value));
            entity.Property(x => x.ProcessedAt).HasConversion(
                value => value.HasValue ? value.Value.ToUnixTimeMilliseconds() : (long?)null,
                value => value.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(value.Value) : null);
            entity.Property(x => x.NextAttemptAt).HasConversion(
                value => value.ToUnixTimeMilliseconds(),
                value => DateTimeOffset.FromUnixTimeMilliseconds(value));
            entity.HasIndex(x => new { x.ProcessedAt, x.NextAttemptAt });
        });
    }
}
