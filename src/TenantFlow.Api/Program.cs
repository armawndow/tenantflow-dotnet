using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TenantFlow.Api;
using TenantFlow.Core;

var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<DevApiKeyOptions>(builder.Configuration.GetSection("DevApiKeys"));
builder.Services.AddTenantFlowCore(builder.Configuration);
builder.Services.AddCors(options => options.AddPolicy("LocalFlutter", policy => policy
    .WithOrigins("http://127.0.0.1:5090", "http://localhost:5090")
    .WithMethods("GET", "POST", "PUT")
    .WithHeaders("Content-Type", "X-Api-Key", "Idempotency-Key", "If-Match")
    .WithExposedHeaders("ETag", "Idempotency-Replayed", "Location")));

var app = builder.Build();
await using (var scope = app.Services.CreateAsyncScope())
{
    await scope.ServiceProvider.GetRequiredService<TenantFlowDbContext>().Database.EnsureCreatedAsync();
}

if (app.Environment.IsDevelopment())
{
    app.UseCors("LocalFlutter");
}

app.UseMiddleware<TenantAuthorizationMiddleware>();
app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

var workItems = app.MapGroup("/api/work-items");
workItems.AddEndpointFilter(TenantRequired);

workItems.MapGet("/", async (WorkItemService service, CancellationToken ct) =>
    Results.Ok(await service.ListAsync(ct)));

workItems.MapGet("/{id:guid}", async (Guid id, WorkItemService service, HttpContext http, CancellationToken ct) =>
{
    var item = await service.GetAsync(id, ct);
    if (item is null) return Results.NotFound();
    http.Response.Headers.ETag = ETag(item.Version);
    return Results.Ok(item);
});

workItems.MapPost("/", async (CreateWorkItem request, WorkItemService service, HttpContext http, CancellationToken ct) =>
{
    var key = http.Request.Headers["Idempotency-Key"].ToString();
    if (string.IsNullOrWhiteSpace(key) || key.Length > 100)
        return Problem(400, "Idempotency-Key must contain 1 to 100 characters.");

    try
    {
        var result = await service.CreateAsync(request, key, ct);
        http.Response.Headers.Location = result.Location;
        http.Response.Headers["Idempotency-Replayed"] = result.Replayed.ToString().ToLowerInvariant();
        return Results.Content(result.Body, "application/json", statusCode: result.StatusCode);
    }
    catch (IdempotencyConflictException ex)
    {
        return Problem(409, ex.Message);
    }
    catch (ArgumentException ex)
    {
        return Problem(400, ex.Message);
    }
});

workItems.MapPut("/{id:guid}", async (Guid id, UpdateWorkItem request, WorkItemService service, HttpContext http, CancellationToken ct) =>
{
    if (!TryParseETag(http.Request.Headers.IfMatch.ToString(), out var expectedVersion))
        return Problem(428, "A single strong numeric If-Match ETag is required.");

    try
    {
        var item = await service.UpdateAsync(id, request, expectedVersion, ct);
        if (item is null) return Results.NotFound();
        http.Response.Headers.ETag = ETag(item.Version);
        return Results.Ok(item);
    }
    catch (StaleVersionException ex)
    {
        return Problem(412, ex.Message);
    }
    catch (ArgumentException ex)
    {
        return Problem(400, ex.Message);
    }
});

var internalApi = app.MapGroup("/internal/outbox");
internalApi.AddEndpointFilter(AdminRequired);
internalApi.MapGet("/", async (string? state, TenantFlowDbContext db, CancellationToken ct) =>
{
    var query = db.OutboxMessages.AsNoTracking().OrderBy(x => x.OccurredAt).AsQueryable();
    query = state?.ToLowerInvariant() switch
    {
        "pending" => query.Where(x => x.ProcessedAt == null),
        "processed" => query.Where(x => x.ProcessedAt != null),
        _ => query
    };
    return Results.Ok(await query.Take(200).ToListAsync(ct));
});
internalApi.MapPost("/dispatch", async (int? maximum, OutboxDispatcher dispatcher, CancellationToken ct) =>
    Results.Ok(await dispatcher.DispatchAsync(maximum ?? 25, ct)));

app.Run();

static ValueTask<object?> TenantRequired(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
{
    var tenant = context.HttpContext.RequestServices.GetRequiredService<ITenantContext>();
    return tenant.Identity.TenantId is not null
        ? next(context)
        : ValueTask.FromResult<object?>(Problem(403, "A tenant API key is required."));
}

static ValueTask<object?> AdminRequired(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
{
    var tenant = context.HttpContext.RequestServices.GetRequiredService<ITenantContext>();
    return tenant.Identity.IsAdmin
        ? next(context)
        : ValueTask.FromResult<object?>(Problem(403, "The local administrator API key is required."));
}

static IResult Problem(int status, string title) => Results.Problem(statusCode: status, title: title);
static string ETag(long version) => $"\"{version}\"";

static bool TryParseETag(string value, out long version)
{
    version = 0;
    return value.Length >= 3
        && value[0] == '"'
        && value[^1] == '"'
        && !value.StartsWith("W/", StringComparison.OrdinalIgnoreCase)
        && long.TryParse(value[1..^1], out version)
        && version > 0;
}

public partial class Program;
