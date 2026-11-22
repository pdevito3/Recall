namespace Recall.Server.Domain.LoginProviders.Features;

using Databases;
using Dtos;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Users;

public static class GetLoginSettings
{
    public sealed record Query : IRequest<LoginSettingsDto>;

    public sealed class Handler(AppDbContext dbContext, IPermissionGuard permissionGuard) : IRequestHandler<Query, LoginSettingsDto>
    {
        public async Task<LoginSettingsDto> Handle(Query request, CancellationToken cancellationToken)
        {
            var caller = await permissionGuard.RequireSignedInPersonAsync(cancellationToken);
            caller.Require(Permission.ManageUserAccess());

            var settings = await dbContext.LoginSettings.AsNoTracking()
                .TagWithOperationCallSite("login_settings.get")
                .SingleAsync(cancellationToken);
            return new LoginSettingsDto { PasswordLogin = settings.PasswordLogin.ToString(), Version = settings.Version };
        }
    }
}
