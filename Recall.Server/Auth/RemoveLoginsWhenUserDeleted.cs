namespace Recall.Server.Auth;

using Databases;
using Domain;
using Domain.Users.DomainEvents;
using Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>A deleted user can sign in no more, on any device. The changes are tracked, so they join the save that deletes the user.</summary>
public sealed class RemoveLoginsWhenUserDeleted : IDomainEventHandler<UserDeleted>
{
    public async Task Handle(UserDeleted domainEvent, AppDbContext dbContext, CancellationToken cancellationToken)
    {
        var userId = domainEvent.UserId;
        dbContext.Set<ExternalLogin>().RemoveRange(await dbContext.Set<ExternalLogin>()
            .TagWithOperationCallSite("auth.user_deleted.logins")
            .Where(l => l.UserId == userId)
            .ToListAsync(cancellationToken));
        dbContext.Set<AuthSession>().RemoveRange(await dbContext.Set<AuthSession>()
            .TagWithOperationCallSite("auth.user_deleted.sessions")
            .Where(s => s.UserId == userId)
            .ToListAsync(cancellationToken));
        dbContext.Set<AppCode>().RemoveRange(await dbContext.Set<AppCode>()
            .TagWithOperationCallSite("auth.user_deleted.app_codes")
            .Where(c => c.UserId == userId)
            .ToListAsync(cancellationToken));
    }
}
