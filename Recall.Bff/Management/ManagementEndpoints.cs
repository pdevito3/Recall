using Recall.Bff.ApiEndpoints;
using Recall.Bff.BackChannel;
using Recall.Bff.Login;
using Recall.Bff.Logout;
using Recall.Bff.User;

namespace Recall.Bff.Management;

public static class ManagementEndpoints
{
    /// <summary>
    /// Maps <c>/bff/login</c>, <c>/bff/logout</c>, <c>/bff/user</c>, and <c>/bff/backchannel</c>. Each one allows
    /// anonymous requests. Only <c>/bff/user</c> is a BFF API endpoint.
    /// </summary>
    public static IEndpointRouteBuilder MapBffManagementEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.Map(LoginEndpoint.Path, LoginEndpoint.HandleAsync).AllowAnonymous();
        endpoints.MapGet(LogoutEndpoint.Path, LogoutEndpoint.HandleAsync).AllowAnonymous();
        endpoints.MapGet(UserEndpoint.Path, UserEndpoint.HandleAsync).AllowAnonymous().AsBffApiEndpoint();
        endpoints.MapPost(BackChannelLogoutEndpoint.Path, BackChannelLogoutEndpoint.HandleAsync).AllowAnonymous();

        return endpoints;
    }
}
