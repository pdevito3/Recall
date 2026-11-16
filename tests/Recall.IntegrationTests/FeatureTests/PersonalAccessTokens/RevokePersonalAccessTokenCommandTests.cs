namespace Recall.IntegrationTests.FeatureTests.PersonalAccessTokens;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Recall.IntegrationTests.Databases;
using Recall.Server.Auth;
using Recall.Server.Auth.Entities;
using Recall.Server.Databases;
using Recall.Server.Domain.Tenants;
using Recall.Server.Domain.AuditEntries;
using Recall.Server.Domain.PersonalAccessTokens;
using Recall.Server.Domain.PersonalAccessTokens.Features;
using Recall.Server.Domain.Users;
using Recall.SharedTestHelpers.EntityGraphs;
using Recall.SharedTestHelpers.Utilities;
using Shouldly;

public class RevokePersonalAccessTokenCommandTests : TestBase
{
    [Test]
    [Property("Rule", "PAT-19")]
    [Arguments("revoke", 1)]
    [Arguments("revoke-others", 0)]
    public async Task a_revoke_that_a_rival_end_beat_keeps_the_rival_end_and_records_no_second_action(string rival, int revokedActions)
    {
        // Arrange
        var pause = PauseAtQuery.After("auth.sessions.revoke_personal_access_token_read");
        await using var services = pause.BuildServices();
        var scope = new TestingServiceScope();
        await scope.StartGraph(out _).AddUser(out var user).BuildAsync();
        var token = await scope.ExecuteScopeAsync(sp => sp.GetRequiredService<SessionStore>().StartPersonalAccessTokenAsync(
            user, PersonalAccessTokenName.Of("Laptop"), new TokenGrant([], JournalAccess.None), null, CancellationToken.None));
        var heldScope = new TestingServiceScope(services);
        heldScope.SetDelegatedClient(user.Identifier, FirstPartyClientOptions.AppClientId);
        var rivalScope = new TestingServiceScope();
        rivalScope.SetDelegatedClient(user.Identifier, FirstPartyClientOptions.AppClientId);

        // Act
        var held = heldScope.SendAsync(new RevokePersonalAccessToken.Command(token.Token.Id));
        await pause.ReachedAsync();
        if (rival == "revoke")
            await rivalScope.SendAsync(new RevokePersonalAccessToken.Command(token.Token.Id));
        else
            await rivalScope.ExecuteScopeAsync(async sp =>
            {
                await sp.GetRequiredService<SessionStore>().RevokeOthersAsync(user.Id, "ses_kept");
                return true;
            });
        var afterRival = await RowAsync(token.Token.Id);
        pause.Release();
        await held;

        // Assert
        afterRival.RevokedAt.ShouldNotBeNull();
        (await RevokedActionsAsync(user.Id)).ShouldBe(revokedActions);
        var end = await RowAsync(token.Token.Id);
        (end.RevokedAt, end.RevokedReason).ShouldBe((afterRival.RevokedAt, afterRival.RevokedReason));
    }

    private static async Task<AuthSession> RowAsync(string tokenId)
    {
        await using var system = TestFixture.BaseScopeFactory.CreateSystemScope();
        return await system.ServiceProvider.GetRequiredService<AppDbContext>().Set<AuthSession>().AsNoTracking().SingleAsync(s => s.Id == tokenId);
    }

    private static async Task<int> RevokedActionsAsync(string userId)
    {
        await using var system = TestFixture.BaseScopeFactory.CreateSystemScope();
        return await system.ServiceProvider.GetRequiredService<AppDbContext>().AuditEntries.AsNoTracking()
            .CountAsync(e => e.RootId == userId && e.Kind == AuditEntryKind.Action && e.Action == "access_token_revoked");
    }
}
