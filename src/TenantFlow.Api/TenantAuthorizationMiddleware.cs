using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using TenantFlow.Core;

namespace TenantFlow.Api;

public sealed class DevApiKeyOptions
{
    public Dictionary<string, string> Tenants { get; init; } = new(StringComparer.Ordinal);
    public string AdminKey { get; init; } = string.Empty;
}

public sealed class TenantAuthorizationMiddleware(RequestDelegate next, IOptions<DevApiKeyOptions> options)
{
    private readonly IReadOnlyDictionary<string, string> _tenantKeys = options.Value.Tenants;
    private readonly string _adminKey = options.Value.AdminKey;

    public async Task InvokeAsync(HttpContext httpContext, ITenantContext tenantContext)
    {
        if (httpContext.Request.Path.StartsWithSegments("/health"))
        {
            await next(httpContext);
            return;
        }

        var presented = httpContext.Request.Headers["X-Api-Key"].ToString();
        if (string.IsNullOrEmpty(presented))
        {
            await WriteProblem(httpContext, StatusCodes.Status401Unauthorized, "API key required");
            return;
        }

        if (!string.IsNullOrEmpty(_adminKey) && FixedTimeEquals(presented, _adminKey))
        {
            tenantContext.Identity = new TenantIdentity(null, true);
        }
        else
        {
            var tenantId = _tenantKeys.FirstOrDefault(pair => FixedTimeEquals(presented, pair.Key)).Value;
            if (tenantId is null)
            {
                await WriteProblem(httpContext, StatusCodes.Status401Unauthorized, "Invalid API key");
                return;
            }

            tenantContext.Identity = new TenantIdentity(tenantId, false);
        }

        await next(httpContext);
    }

    private static bool FixedTimeEquals(string left, string right)
    {
        var leftHash = SHA256.HashData(Encoding.UTF8.GetBytes(left));
        var rightHash = SHA256.HashData(Encoding.UTF8.GetBytes(right));
        return CryptographicOperations.FixedTimeEquals(leftHash, rightHash);
    }

    private static Task WriteProblem(HttpContext context, int status, string title)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(new { type = "about:blank", title, status });
    }
}
