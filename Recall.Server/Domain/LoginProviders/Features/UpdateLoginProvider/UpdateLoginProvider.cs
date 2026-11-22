namespace Recall.Server.Domain.LoginProviders.Features;

using System.Diagnostics;
using Auth;
using Databases;
using Dtos;
using Exceptions;
using Mappings;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Resources;
using Serilog;
using Users;

public static class UpdateLoginProvider
{
    public sealed record Command(string Id, LoginProviderForUpdateDto Dto) : IRequest<LoginProviderDto>;

    public sealed class Handler(AppDbContext dbContext, IPermissionGuard permissionGuard, LoginModes modes, LoginProviderSecret secrets)
        : IRequestHandler<Command, LoginProviderDto>
    {
        private readonly ILogger _logger = Log.ForContext<Handler>();

        public async Task<LoginProviderDto> Handle(Command request, CancellationToken cancellationToken)
        {
            Activity.Current?.SetTag("app.login_provider_id", request.Id);
            var caller = await permissionGuard.RequireSignedInPersonAsync(cancellationToken);
            caller.Require(Permission.ManageUserAccess());

            if (!StorableText.CanStore(request.Id))
                throw new NotFoundException();
            var provider = await dbContext.LoginProviders
                .TagWithOperationCallSite("login_providers.update.load")
                .FirstOrDefaultAsync(p => p.Id == request.Id, cancellationToken) ?? throw new NotFoundException();

            var secret = LoginProvider.ClientSecretOf(request.Dto.ClientSecret);
            provider.Update(request.Dto.ToForm(), secret is null ? null : secrets.Protect(secret), request.Dto.Version);
            dbContext.RecordAction(new LoginProviderUpdated(
                await LoginProviderAudit.SubjectAsync(dbContext, cancellationToken),
                provider.Id, provider.DisplayName, provider.ClientId, provider.AllowHttp, provider.CaCertificatePath, secret is not null));
            await dbContext.SaveChangesAsync(cancellationToken);

            try
            {
                await modes.ReloadAsync(CancellationToken.None);
            }
            // The row is saved, and the next save or the next start lists it. The caller gets a 500, so the Admin reloads the page.
            catch (Exception exception)
            {
                _logger.Here().Error(exception, "Updated login provider {LoginProviderId}, but the reload of the login modes failed, so the login page does not show the change yet", provider.Id);
                throw;
            }

            _logger.Here().Information("Updated login provider {LoginProviderId}, new secret {SecretChanged}", provider.Id, secret is not null);
            return provider.ToDto(modes.Environment, secrets);
        }
    }
}
