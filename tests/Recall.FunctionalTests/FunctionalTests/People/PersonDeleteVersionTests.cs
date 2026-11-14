namespace Recall.FunctionalTests.FunctionalTests.People;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Recall.Server.Domain.People.Dtos;
using Sessions;
using Shouldly;
using TestUtilities;

public class PersonDeleteVersionTests : TestBase
{
    private const string StaleVersionType = "urn:recall:problem:stale-version";

    [Test]
    [Property("Rule", "SES-47")]
    public async Task a_delete_at_a_version_that_a_rename_moved_gives_409_and_keeps_the_person()
    {
        // Arrange
        var seen = await SignInWithPersonAsync();
        var renamed = await FactoryClient.PutAsJsonAsync(ApiRoutes.People.GetRecord(seen.Id), new { label = "Samuel" });
        renamed.StatusCode.ShouldBe(HttpStatusCode.OK);

        // Act
        var refused = await FactoryClient.DeleteAsync(DeleteAt(seen.Id, seen.Version));
        var problem = await refused.Content.ReadFromJsonAsync<JsonElement>();
        var stored = await FactoryClient.GetFromJsonAsync<PersonDto>(ApiRoutes.People.GetRecord(seen.Id));

        // Assert
        refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        problem.GetProperty("type").GetString().ShouldBe(StaleVersionType);
        problem.GetProperty("errors").TryGetProperty("version", out _).ShouldBeTrue();
        stored!.Label.ShouldBe("Samuel");
    }

    [Test]
    [Property("Rule", "SES-47")]
    public async Task a_delete_at_the_stored_version_or_with_no_version_removes_the_person()
    {
        // Arrange
        var atVersion = await SignInWithPersonAsync();
        var created = await FactoryClient.PostAsJsonAsync(ApiRoutes.People.Root, new { label = "Ana" });
        var anyVersion = (await created.Content.ReadFromJsonAsync<PersonDto>())!;
        await FactoryClient.PutAsJsonAsync(ApiRoutes.People.GetRecord(anyVersion.Id), new { label = "Anna" });

        // Act
        var first = await FactoryClient.DeleteAsync(DeleteAt(atVersion.Id, atVersion.Version));
        var second = await FactoryClient.DeleteAsync(ApiRoutes.People.GetRecord(anyVersion.Id));

        // Assert
        first.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        second.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await FactoryClient.GetAsync(ApiRoutes.People.GetRecord(atVersion.Id))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await FactoryClient.GetAsync(ApiRoutes.People.GetRecord(anyVersion.Id))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private static string DeleteAt(string id, uint version) => $"{ApiRoutes.People.GetRecord(id)}?ifVersion={version}";

    private async Task<PersonDto> SignInWithPersonAsync()
    {
        var (_, member) = await JournalScenario.AddPeopleAsync();
        FactoryClient.SignInAsAppUser(member.Identifier);
        var created = await FactoryClient.PostAsJsonAsync(ApiRoutes.People.Root, new { label = "Sam" });
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await created.Content.ReadFromJsonAsync<PersonDto>())!;
    }
}
