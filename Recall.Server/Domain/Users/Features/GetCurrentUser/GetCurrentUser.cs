namespace Recall.Server.Domain.Users.Features;

using Databases;
using Dtos;
using Exceptions;
using MediatR;
using Microsoft.EntityFrameworkCore;

public static class GetCurrentUser
{
    public sealed record Query : IRequest<CurrentUserDto>;

    public sealed class Handler(ICurrentUserService currentUserService, AppDbContext dbContext) : IRequestHandler<Query, CurrentUserDto>
    {
        public async Task<CurrentUserDto> Handle(Query request, CancellationToken cancellationToken)
        {
            var identifier = currentUserService.UserIdentifier
                ?? throw new ForbiddenAccessException("Only a signed-in user can do this. The token has no subject.");

            var name = currentUserService.Name;
            if (name is null)
            {
                var user = await dbContext.Users
                    .AsNoTracking()
                    .TagWithOperationCallSite("users.current.find_name")
                    .FirstOrDefaultAsync(u => u.Identifier == identifier, cancellationToken);
                name = user?.FullName;
            }

            return new CurrentUserDto { Identifier = identifier, Name = name };
        }
    }
}
