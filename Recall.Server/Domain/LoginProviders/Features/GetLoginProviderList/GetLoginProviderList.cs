namespace Recall.Server.Domain.LoginProviders.Features;

using Auth;
using Databases;
using Dtos;
using Mappings;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Users;

public static class GetLoginProviderList
{
    public sealed record Query(string CallbackUrl) : IRequest<LoginProviderListDto>;

    public sealed class Handler(AppDbContext dbContext, IPermissionGuard permissionGuard, LoginModes modes, LoginProviderSecret secrets)
        : IRequestHandler<Query, LoginProviderListDto>
    {
        public async Task<LoginProviderListDto> Handle(Query request, CancellationToken cancellationToken)
        {
            var caller = await permissionGuard.RequireSignedInPersonAsync(cancellationToken);
            caller.Require(Permission.ManageUserAccess());

            var rows = await dbContext.LoginProviders.AsNoTracking()
                .TagWithOperationCallSite("login_providers.list")
                .OrderBy(p => p.CreatedAt).ThenBy(p => p.Id)
                .ToListAsync(cancellationToken);
            return new LoginProviderListDto
            {
                Items = rows.Select(row => row.ToDto(modes.Environment, secrets)).ToList(),
                EnvironmentProviders = modes.Environment
                    .Select((entry, position) => (Id: AuthOptions.ModeIdOf(entry, position), Entry: entry))
                    .Select(e => new EnvironmentLoginProviderDto
                    {
                        Id = e.Id,
                        DisplayName = string.IsNullOrWhiteSpace(e.Entry.DisplayName) ? e.Id : e.Entry.DisplayName,
                        Issuer = e.Entry.Issuer!,
                    })
                    .ToList(),
                CallbackUrl = request.CallbackUrl,
            };
        }
    }
}
