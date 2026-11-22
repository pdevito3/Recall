namespace Recall.Server.Domain.LoginProviders;

using AuditEntries;
using Databases;
using Exceptions;

public static class LoginProviderAudit
{
    /// <summary>The tenant of the caller, the subject of each action on a provider. A provider has no tenant, so the action goes to the tenant of the Admin who made it.</summary>
    public static async Task<AuditSubject> SubjectAsync(AppDbContext dbContext, CancellationToken cancellationToken)
    {
        var tenantId = dbContext.CurrentTenantScope.TenantId ?? throw new ForbiddenAccessException("Only a person signed in to Recall can do this.");
        var tenant = await dbContext.Tenants
            .TagWithOperationCallSite("login_providers.audit.tenant")
            .GetById(tenantId, cancellationToken);
        return AuditSubject.Of(tenant);
    }
}
