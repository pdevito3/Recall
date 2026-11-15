namespace Recall.FunctionalTests.FunctionalTests.Auth;

using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Recall.FunctionalTests.FunctionalTests.Sessions;
using Recall.Server.Domain.AuditEntries;
using Recall.Server.Domain.People.Dtos;
using Recall.Server.Domain.Users;
using Shouldly;
using TestUtilities;

/// <summary>
/// A personal access token through the test principal of the <c>AppSession</c> scheme, as the Server will issue it.
/// Each test arranges the journal as the person in an app session, then calls as a token of that person.
/// </summary>
public class PersonalAccessTokenCallerTests : TestBase
{
    private static TokenGrant Journal(JournalAccess access, params Permission[] permissions) => new(permissions, access);

    private async Task<PersonDto> AddPersonAsync(string label)
    {
        var added = await FactoryClient.PostAsJsonAsync(ApiRoutes.People.Root, new { label });
        added.StatusCode.ShouldBe(HttpStatusCode.Created, await added.Content.ReadAsStringAsync());
        return (await added.Content.ReadFromJsonAsync<PersonDto>()).ShouldNotBeNull();
    }

    private static async Task ShouldBeForbiddenAsync(HttpResponseMessage response, string detail)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await response.Content.ReadFromJsonAsync<ProblemDetails>()).ShouldNotBeNull().Detail.ShouldBe(detail);
    }

    [Test]
    [Property("Rule", "PAT-6")]
    public async Task a_token_with_no_journal_access_gets_404_by_id_and_403_for_a_list()
    {
        // Arrange
        var (_, member) = await JournalScenario.AddPeopleAsync();
        FactoryClient.SignInAsAppUser(member.Identifier);
        var session = await FactoryClient.AddSessionAsync();
        var person = await AddPersonAsync("Sam");
        FactoryClient.SignInAsPersonalAccessToken(member.Identifier, Journal(JournalAccess.None, Permission.GetAll().ToArray()));

        // Act
        var sessionById = await FactoryClient.GetAsync(ApiRoutes.Sessions.GetRecord(session.Id));
        var personById = await FactoryClient.GetAsync(ApiRoutes.People.GetRecord(person.Id));
        var sessions = await FactoryClient.GetAsync(ApiRoutes.Sessions.Root);
        var settings = await FactoryClient.GetAsync(ApiRoutes.UserSettings.Root);

        // Assert
        sessionById.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        personById.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        sessions.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        settings.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Test]
    [Property("Rule", "PAT-4")]
    public async Task a_permission_that_the_user_loses_refuses_the_next_call_of_the_token()
    {
        // Arrange
        var (admin, _) = await JournalScenario.AddPeopleAsync();
        FactoryClient.SignInAsPersonalAccessToken(admin.Identifier, Journal(JournalAccess.None, Permission.ReadUsers()));
        var before = await FactoryClient.GetAsync(ApiRoutes.Users.GetList);
        await ExecuteDbContextAsync(async db =>
        {
            var user = await db.Users.Include(u => u.UserPermissions).SingleAsync(u => u.Id == admin.Id);
            user.RemovePermission(Permission.ReadUsers());
            await db.SaveChangesAsync();
        });

        // Act
        var after = await FactoryClient.GetAsync(ApiRoutes.Users.GetList);

        // Assert
        before.StatusCode.ShouldBe(HttpStatusCode.OK);
        await ShouldBeForbiddenAsync(after, $"You cannot {Permission.ReadUsers().Words}. Ask an admin for access.");
    }

    [Test]
    [Property("Rule", "PAT-4")]
    public async Task a_token_holds_no_permission_that_its_grant_lacks()
    {
        // Arrange
        var (admin, _) = await JournalScenario.AddPeopleAsync();
        FactoryClient.SignInAsPersonalAccessToken(admin.Identifier, Journal(JournalAccess.ReadWrite, Permission.ManageUsers()));

        // Act
        var list = await FactoryClient.GetAsync(ApiRoutes.Users.GetList);

        // Assert
        await ShouldBeForbiddenAsync(list,
            $"This personal access token has no grant to {Permission.ReadUsers().Words}. Make a token that has it.");
    }

    [Test]
    [Property("Rule", "PAT-14")]
    public async Task each_write_of_a_token_records_the_token_as_the_client_and_the_person_as_the_user()
    {
        // Arrange
        var (admin, member) = await JournalScenario.AddPeopleAsync();
        var memberToken = FactoryClient.SignInAsPersonalAccessToken(member.Identifier, Journal(JournalAccess.ReadWrite));
        var person = await AddPersonAsync("Sam");
        (await FactoryClient.DeleteAsync(ApiRoutes.People.GetRecord(person.Id))).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var adminToken = FactoryClient.SignInAsPersonalAccessToken(admin.Identifier,
            Journal(JournalAccess.None, Permission.ManageUsers(), Permission.ReadUsers()));

        // Act
        var deleted = await FactoryClient.DeleteRequestAsync(ApiRoutes.Users.Delete(member.Id));
        var entries = await ExecuteDbContextAsync(db => db.AuditEntries.IgnoreQueryFilters()
            .Where(e => e.EntityId == person.Id || (e.EntityId == member.Id && e.Kind == AuditEntryKind.Deleted))
            .Select(e => new { e.EntityId, e.Kind, e.ActorClient, e.ActorUserId })
            .ToListAsync());

        // Assert
        deleted.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        entries.Where(e => e.EntityId == person.Id).Select(e => (e.Kind, e.ActorClient, e.ActorUserId)).ShouldBe(
        [
            (AuditEntryKind.Created, memberToken, member.Id),
            (AuditEntryKind.Deleted, memberToken, member.Id),
        ], ignoreOrder: true);
        entries.Where(e => e.EntityId == member.Id).Select(e => (e.ActorClient, e.ActorUserId))
            .ShouldBe([(adminToken, admin.Id)]);
    }
}
