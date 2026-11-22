namespace Recall.Server.Domain.LoginProviders.Features;

using System.Diagnostics;
using Auth;
using Databases;
using Dtos;
using Exceptions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Resources;
using Serilog;
using Users;

public static class UpdateLoginSettings
{
    public sealed record Command(LoginSettingsForUpdateDto Dto) : IRequest<LoginSettingsDto>;

    public sealed class Handler(AppDbContext dbContext, IPermissionGuard permissionGuard, LoginModes modes) : IRequestHandler<Command, LoginSettingsDto>
    {
        private readonly ILogger _logger = Log.ForContext<Handler>();

        public async Task<LoginSettingsDto> Handle(Command request, CancellationToken cancellationToken)
        {
            var caller = await permissionGuard.RequireSignedInPersonAsync(cancellationToken);
            caller.Require(Permission.ManageUserAccess());
            Activity.Current?.SetTag("app.user_id", caller.UserId);
            var state = StateOf(request.Dto.PasswordLogin);

            // A turn changes the ways to sign in of each tenant, so it holds the lock of each live tenant, and each guard of one tenant
            // waits for it and reads the new state (IDP-13, IDP-14).
            var settings = await LastAdmin.RunWithEachTenantLockAsync(dbContext, async ct =>
            {
                var settings = await dbContext.LoginSettings
                    .TagWithOperationCallSite("login_settings.update.load")
                    .SingleAsync(ct);
                var subject = await LoginProviderAudit.SubjectAsync(dbContext, ct);
                Domain.AuditEntries.AuditAction? turned = state == PasswordLoginState.Off
                    ? settings.TurnPasswordsOff(
                        request.Dto.Version,
                        settings.PasswordLogin == PasswordLoginState.Off
                            || LastAdmin.EachTenantHasALinkedAdmin(await LastAdmin.LoadAdminLinksAsync(dbContext, ct), modes.UsableIssuers),
                        subject)
                    : settings.TurnPasswordsOn(request.Dto.Version, subject);
                if (turned is null)
                    return settings;

                dbContext.RecordAction(turned);
                await dbContext.SaveChangesAsync(ct);

                // In the locks, so a guard that waits reads the new state. A failed reload still sets the state, so a turn to Off
                // stops each password at once.
                try
                {
                    await modes.ReloadAsync(CancellationToken.None);
                }
                catch (Exception exception)
                {
                    _logger.Here().Error(exception, "Turned password login {PasswordLogin}, but the reload of the login modes failed, so only the setting changed", state);
                    await modes.UsePasswordLoginAsync(state);
                }
                return settings;
            }, cancellationToken);

            _logger.Here().Information("Password login is {PasswordLogin}", settings.PasswordLogin);
            return new LoginSettingsDto { PasswordLogin = settings.PasswordLogin.ToString(), Version = settings.Version };
        }

        private static PasswordLoginState StateOf(string? typed)
        {
            ValidationException.Must(
                Enum.TryParse<PasswordLoginState>(typed, out var state) && Enum.GetName(state) == typed,
                LoginSettings.PasswordLoginField, "Give On or Off.");
            return state;
        }
    }
}
