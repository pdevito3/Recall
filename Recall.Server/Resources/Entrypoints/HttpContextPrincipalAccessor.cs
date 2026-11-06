namespace Recall.Server.Resources.Entrypoints;

using System.Security.Claims;
using Domain.Users;

public sealed class HttpContextPrincipalAccessor(IHttpContextAccessor httpContextAccessor) : ICurrentPrincipalAccessor
{
    public ClaimsPrincipal? Principal => httpContextAccessor.HttpContext?.User;
}
