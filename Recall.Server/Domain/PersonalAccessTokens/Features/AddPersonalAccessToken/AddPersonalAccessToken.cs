namespace Recall.Server.Domain.PersonalAccessTokens.Features;

using System.Diagnostics;
using Auth;
using Databases;
using Dtos;
using Exceptions;
using Mappings;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Users;

public static class AddPersonalAccessToken
{
    public sealed record Command(PersonalAccessTokenForCreationDto Dto) : IRequest<PersonalAccessTokenCreatedDto>;

    public const string OnlyTheOwnerGivesJobTools = "Only the Owner of this install can give a token the job tools. Make the token without them.";

    public sealed class Handler(
        AppDbContext dbContext, IPermissionGuard permissionGuard, IOwnerGuard ownerGuard, SessionStore store, TimeProvider timeProvider)
        : IRequestHandler<Command, PersonalAccessTokenCreatedDto>
    {
        public async Task<PersonalAccessTokenCreatedDto> Handle(Command request, CancellationToken cancellationToken)
        {
            var member = await permissionGuard.RequireSignedInPersonAsync(cancellationToken);
            Activity.Current?.SetTag("app.user_id", member.UserId);
            var permissions = (request.Dto.Permissions ?? []).Select((name, i) => Permission.Of(name, $"permissions[{i}]")).ToList();
            var journal = JournalAccess.Of(request.Dto.ReadJournal, request.Dto.WriteJournal);
            member.Require(permissions);
            var jobTools = request.Dto.JobTools == true;
            if (jobTools && await ownerGuard.CallerIfOwnerAsync(cancellationToken) is null)
                throw new ForbiddenAccessException(OnlyTheOwnerGivesJobTools);

            var name = PersonalAccessTokenName.Of(request.Dto.Name);
            var now = timeProvider.GetUtcNow();
            var expiresAt = PersonalAccessTokenExpiry.Of(request.Dto.ExpiresAt, request.Dto.NeverExpires == true, now);
            var user = await dbContext.Users
                .TagWithOperationCallSite("personal_access_tokens.add.user")
                .AsNoTracking()
                .GetById(member.UserId, cancellationToken);

            var created = await store.StartPersonalAccessTokenAsync(user, name, new TokenGrant(permissions, journal, jobTools), expiresAt, cancellationToken);
            Activity.Current?.SetTag("app.personal_access_token_id", created.Token.Id);
            return new PersonalAccessTokenCreatedDto { Token = created.Token.ToPersonalAccessTokenDto(member, now), Value = created.Value };
        }
    }
}
