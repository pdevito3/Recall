namespace Recall.IntegrationTests.FeatureTests.Users;

using Recall.Server.Auth;
using Recall.Server.Auth.Entities;
using Recall.Server.Domain.Users;

internal static class SignInLinks
{
    public static Task AddSignInLinkAsync(this TestingServiceScope scope, User user)
        => scope.ExecuteDbContextAsync(async db =>
        {
            db.Set<ExternalLogin>().Add(new ExternalLogin
            {
                Issuer = TestFixture.LinkIssuer,
                Subject = user.Id,
                UserId = user.Id,
                CreatedAt = DateTimeOffset.UtcNow,
                LastLoginAt = DateTimeOffset.UtcNow,
            });
            return await db.SaveChangesAsync();
        });

    /// <summary>A Chosen password credential, or a Stopped one with <paramref name="stopped"/>. The hash is not a real hash, because no test here signs in.</summary>
    public static Task AddPasswordAsync(this TestingServiceScope scope, User user, bool stopped = false)
        => scope.ExecuteDbContextAsync(async db =>
        {
            LoginName.TryOf($"login-{user.Id}", out var name);
            var credential = PasswordCredential.Choose(user.Id, name!, "not-a-hash", DateTimeOffset.UtcNow);
            for (var i = 0; stopped && i < PasswordCredential.StopAt; i++)
                credential.NoteFailure();
            db.Set<PasswordCredential>().Add(credential);
            return await db.SaveChangesAsync();
        });
}
