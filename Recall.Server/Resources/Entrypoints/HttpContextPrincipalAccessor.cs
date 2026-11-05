namespace Recall.Server.Resources.Entrypoints;

using System.Security.Claims;
using Domain.Users;

/// <summary>
/// Gives the core the principal of the current HTTP request. HTTP and MCP each run in an HTTP request, and
/// the JWT bearer handler sets its user, so both transports give the same caller.
/// </summary>
public sealed class HttpContextPrincipalAccessor(IHttpContextAccessor httpContextAccessor) : ICurrentPrincipalAccessor
{
    public ClaimsPrincipal? Principal => httpContextAccessor.HttpContext?.User;
}
