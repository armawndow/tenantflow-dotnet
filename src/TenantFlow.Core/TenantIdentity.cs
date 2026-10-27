namespace TenantFlow.Core;

public sealed record TenantIdentity(string? TenantId, bool IsAdmin)
{
    public static TenantIdentity Anonymous { get; } = new(null, false);
}

public interface ITenantContext
{
    TenantIdentity Identity { get; set; }
    string RequireTenantId();
}

public sealed class TenantContext : ITenantContext
{
    public TenantIdentity Identity { get; set; } = TenantIdentity.Anonymous;

    public string RequireTenantId() => Identity.TenantId
        ?? throw new InvalidOperationException("A tenant identity is required for this operation.");
}
