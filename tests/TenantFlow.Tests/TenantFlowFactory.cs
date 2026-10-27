using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TenantFlow.Core;

namespace TenantFlow.Tests;

public sealed class TenantFlowFactory(string environment = "Testing") : WebApplicationFactory<Program>
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"tenantflow-{Guid.NewGuid():N}.db");

    public RecordingPublisher Publisher { get; } = new();
    public ManualTimeProvider Clock { get; } = new(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["ConnectionStrings:TenantFlow"] = $"Data Source={_databasePath}",
                ["DevApiKeys:Tenants:alpha-test-key"] = "alpha",
                ["DevApiKeys:Tenants:beta-test-key"] = "beta",
                ["DevApiKeys:AdminKey"] = "admin-test-key",
                ["Events:Provider"] = "Local"
            }));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<TenantFlowDbContext>();
            services.RemoveAll<DbContextOptions<TenantFlowDbContext>>();
            services.RemoveAll<IEventPublisher>();
            services.RemoveAll<TimeProvider>();
            services.AddDbContext<TenantFlowDbContext>(options => options.UseSqlite($"Data Source={_databasePath};Pooling=False"));
            services.AddSingleton<IEventPublisher>(Publisher);
            services.AddSingleton<TimeProvider>(Clock);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (File.Exists(_databasePath)) File.Delete(_databasePath);
        if (File.Exists(_databasePath + "-wal")) File.Delete(_databasePath + "-wal");
        if (File.Exists(_databasePath + "-shm")) File.Delete(_databasePath + "-shm");
    }
}

public sealed class RecordingPublisher : IEventPublisher
{
    private readonly List<Guid> _published = [];
    public bool Fail { get; set; }
    public IReadOnlyList<Guid> Published => _published;

    public Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        if (Fail) throw new InvalidOperationException("Synthetic transport failure.");
        lock (_published) _published.Add(message.Id);
        return Task.CompletedTask;
    }
}

public sealed class ManualTimeProvider(DateTimeOffset initial) : TimeProvider
{
    private DateTimeOffset _utcNow = initial;
    public override DateTimeOffset GetUtcNow() => _utcNow;
    public void Advance(TimeSpan duration) => _utcNow = _utcNow.Add(duration);
}
