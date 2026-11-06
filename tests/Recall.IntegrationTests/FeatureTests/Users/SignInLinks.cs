namespace Recall.IntegrationTests.FeatureTests.Users;

using Recall.Server.Auth.Entities;
using Recall.Server.Domain.Users;

internal static class SignInLinks
{
    public static Task AddSignInLinkAsync(this TestingServiceScope scope, User user)
        => scope.ExecuteDbContextAsync(async db =>
        {
            db.Set<ExternalLogin>().Add(new ExternalLogin
            {
                Issuer = "https://provider.test",
                Subject = user.Id,
                UserId = user.Id,
                CreatedAt = DateTimeOffset.UtcNow,
                LastLoginAt = DateTimeOffset.UtcNow,
            });
            return await db.SaveChangesAsync();
        });
}
