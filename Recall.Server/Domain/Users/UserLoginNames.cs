namespace Recall.Server.Domain.Users;

using Auth;
using Auth.Entities;
using Databases;
using Microsoft.EntityFrameworkCore;

/// <summary>The login name of a user with a password credential (PWD-18), which <see cref="Dtos.UserDto.LoginName"/> shows.</summary>
public static class UserLoginNames
{
    public static Task<LoginName?> OfAsync(AppDbContext dbContext, string userId, CancellationToken cancellationToken)
        => dbContext.Set<PasswordCredential>()
            .TagWithOperationCallSite("users.login_name")
            .AsNoTracking()
            .Where(c => c.UserId == userId)
            .Select(c => (LoginName?)c.LoginName)
            .FirstOrDefaultAsync(cancellationToken);

    public static async Task<IReadOnlyDictionary<string, LoginName>> OfAsync(
        AppDbContext dbContext, IReadOnlyCollection<string> userIds, CancellationToken cancellationToken)
        => await dbContext.Set<PasswordCredential>()
            .TagWithOperationCallSite("users.login_names")
            .AsNoTracking()
            .Where(c => userIds.Contains(c.UserId))
            .ToDictionaryAsync(c => c.UserId, c => c.LoginName, cancellationToken);
}
