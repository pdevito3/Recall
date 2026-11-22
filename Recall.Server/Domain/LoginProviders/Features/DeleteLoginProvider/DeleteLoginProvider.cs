namespace Recall.Server.Domain.LoginProviders.Features;

using System.Diagnostics;
using Auth;
using Databases;
using Databases.Sqlite;
using Exceptions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Resources;
using Serilog;
using Users;

public static class DeleteLoginProvider
{
    public sealed record Command(string Id) : IRequest;

    public sealed class Handler(AppDbContext dbContext, IPermissionGuard permissionGuard, LoginModes modes, WalScrub walScrub) : IRequestHandler<Command>
    {
        private readonly ILogger _logger = Log.ForContext<Handler>();

        public async Task Handle(Command request, CancellationToken cancellationToken)
        {
            Activity.Current?.SetTag("app.login_provider_id", request.Id);
            var caller = await permissionGuard.RequireSignedInPersonAsync(cancellationToken);
            caller.Require(Permission.ManageUserAccess());
            if (!StorableText.CanStore(request.Id))
                throw new NotFoundException();

            // The delete can take the last way in from any tenant, so it holds the lock of each live tenant, and each guard of one tenant
            // waits for it (IDP-14).
            await LastAdmin.RunWithEachTenantLockAsync(dbContext, async ct =>
            {
                var provider = await dbContext.LoginProviders
                    .TagWithOperationCallSite("login_providers.delete.load")
                    .FirstOrDefaultAsync(p => p.Id == request.Id, ct) ?? throw new NotFoundException();

                await LastAdmin.GuardEachTenantAsync(dbContext, modes.UsableIssuers, modes.UsableIssuersWithout(provider.Id), modes.PasswordsAreOn, ct);
                dbContext.LoginProviders.Remove(provider);
                dbContext.RecordAction(new LoginProviderDeleted(await LoginProviderAudit.SubjectAsync(dbContext, ct), provider.Id, provider.Issuer));
                await dbContext.SaveChangesAsync(ct);

                // In the locks, so a guard that waits reads the list with no row. A failed reload still takes the row off the list,
                // so a deleted provider stops signing people in.
                try
                {
                    await modes.ReloadAsync(CancellationToken.None);
                }
                catch (Exception exception)
                {
                    _logger.Here().Error(exception, "Deleted login provider {LoginProviderId}, but the reload of the login modes failed, so only that provider left the list", provider.Id);
                    await modes.ForgetRowAsync(provider.Id);
                }
                return true;
            }, cancellationToken);

            var scrubbed = await walScrub.TruncateAsync(dbContext.Database.GetConnectionString()!);
            _logger.Here().Information("Deleted login provider {LoginProviderId}, WAL scrubbed {WalScrubbed}", request.Id, scrubbed);
        }
    }
}
