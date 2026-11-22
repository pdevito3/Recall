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

public static class AddLoginProvider
{
    public sealed record Command(LoginProviderForCreationDto Dto) : IRequest<LoginProviderDto>;

    public sealed class Handler(
        AppDbContext dbContext, IPermissionGuard permissionGuard, LoginModes modes, LoginProviderSecret secrets, TimeProvider timeProvider)
        : IRequestHandler<Command, LoginProviderDto>
    {
        private readonly ILogger _logger = Log.ForContext<Handler>();

        public async Task<LoginProviderDto> Handle(Command request, CancellationToken cancellationToken)
        {
            var caller = await permissionGuard.RequireSignedInPersonAsync(cancellationToken);
            caller.Require(Permission.ManageUserAccess());

            var secret = LoginProvider.ClientSecretOf(request.Dto.ClientSecret);
            ValidationException.Must(secret is not null, LoginProvider.ClientSecretField, "Give the client secret that the provider gave Recall.");
            var provider = LoginProvider.Add(request.Dto.ToForm(), request.Dto.Id, secrets.Protect(secret!), timeProvider.GetUtcNow());
            Activity.Current?.SetTag("app.login_provider_id", provider.Id);

            // A handful of rows, so the check reads each of them. The unique indexes stop two adds that race.
            var taken = await dbContext.LoginProviders.AsNoTracking()
                .TagWithOperationCallSite("login_providers.add.taken")
                .Select(p => new { p.Id, p.Issuer })
                .ToListAsync(cancellationToken);
            var environment = modes.Environment.Select((entry, position) => (Id: AuthOptions.ModeIdOf(entry, position), entry.Issuer)).ToList();
            if (environment.Any(e => e.Id == provider.Id) || taken.Any(t => t.Id == provider.Id))
                throw new ConflictException(LoginProvider.IdField, LoginProvider.IdInUseMessage);
            if (environment.Any(e => LoginProvider.SameIssuer(e.Issuer, provider.Issuer)) || taken.Any(t => LoginProvider.SameIssuer(t.Issuer, provider.Issuer)))
                throw new ConflictException(LoginProvider.IssuerField, LoginProvider.IssuerInUseMessage);

            dbContext.LoginProviders.Add(provider);
            dbContext.RecordAction(new LoginProviderAdded(
                await LoginProviderAudit.SubjectAsync(dbContext, cancellationToken),
                provider.Id, provider.DisplayName, provider.Issuer, provider.ClientId, provider.AllowHttp, provider.CaCertificatePath));
            await dbContext.SaveChangesAsync(cancellationToken);

            // The save committed, so the list changes also when the caller goes away now (IDP-1).
            try
            {
                await modes.ReloadAsync(CancellationToken.None);
            }
            // The row is saved, and the next save or the next start lists it. The caller gets a 500, so the Admin reloads the page.
            catch (Exception exception)
            {
                _logger.Here().Error(exception, "Added login provider {LoginProviderId}, but the reload of the login modes failed, so the login page does not show the change yet", provider.Id);
                throw;
            }

            _logger.Here().Information("Added login provider {LoginProviderId}", provider.Id);
            return provider.ToDto(modes.Environment, secrets);
        }
    }
}
