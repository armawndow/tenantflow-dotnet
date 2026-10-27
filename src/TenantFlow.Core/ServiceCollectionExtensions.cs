using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace TenantFlow.Core;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddTenantFlowCore(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("TenantFlow") ?? "Data Source=tenantflow.db";
        services.AddDbContext<TenantFlowDbContext>(options => options.UseSqlite(connectionString));
        services.AddScoped<ITenantContext, TenantContext>();
        services.AddSingleton<IdempotencyCoordinator>();
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<WorkItemService>();
        services.AddScoped<OutboxDispatcher>();
        services.AddSingleton<OutboxDispatchCoordinator>();

        var provider = configuration["Events:Provider"] ?? "Local";
        if (provider.Equals("AzureServiceBus", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<IEventPublisher>(_ => new AzureServiceBusEventPublisher(
                configuration["Events:AzureServiceBus:ConnectionString"]
                    ?? throw new InvalidOperationException("Azure Service Bus connection string is required."),
                configuration["Events:AzureServiceBus:Topic"]
                    ?? throw new InvalidOperationException("Azure Service Bus topic is required.")));
        }
        else
        {
            services.AddSingleton<IEventPublisher, LocalEventPublisher>();
        }

        return services;
    }
}
