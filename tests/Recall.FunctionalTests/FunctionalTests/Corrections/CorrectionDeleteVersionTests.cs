namespace Recall.FunctionalTests.FunctionalTests.Corrections;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Recall.Server.Domain.Corrections.Dtos;
using Sessions;
using Shouldly;
using TestUtilities;

public class CorrectionDeleteVersionTests : TestBase
{
    private const string StaleVersionType = "urn:recall:problem:stale-version";

    private static readonly string[] Seen = ["tall in"];
    private static readonly string[] WebEdit = ["tall in", "tal in"];

    [Test]
    public async Task a_delete_at_a_version_that_an_edit_moved_gives_409_and_keeps_the_edit_in_the_row_and_the_feed()
    {
        // Arrange
        var seen = await SignInWithCorrectionAsync();
        var edited = await FactoryClient.PutAsJsonAsync(ApiRoutes.Corrections.GetRecord(seen.Id),
            new { canonicalTerm = "Tallinn", mishearings = WebEdit, version = seen.Version });
        edited.StatusCode.ShouldBe(HttpStatusCode.OK);

        // Act
        var refused = await FactoryClient.DeleteAsync(DeleteAt(seen.Id, seen.Version));
        var problem = await refused.Content.ReadFromJsonAsync<JsonElement>();
        var stored = await FactoryClient.GetFromJsonAsync<CorrectionDto>(ApiRoutes.Corrections.GetRecord(seen.Id));
        var feed = await FactoryClient.PullAsync();

        // Assert
        refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        problem.GetProperty("type").GetString().ShouldBe(StaleVersionType);
        problem.GetProperty("errors").TryGetProperty("version", out _).ShouldBeTrue();
        stored!.Mishearings.ShouldBe(["tall in", "tal in"]);
        stored.Version.ShouldBe(seen.Version + 1);
        feed.Corrections.ShouldHaveSingleItem().Mishearings.ShouldBe(["tall in", "tal in"]);
        feed.Tombstones.ShouldNotContain(t => t.Id == seen.Id);
    }

    [Test]
    public async Task a_delete_at_the_stored_version_removes_the_correction_and_the_feed_has_its_tombstone()
    {
        // Arrange
        var seen = await SignInWithCorrectionAsync();

        // Act
        var deleted = await FactoryClient.DeleteAsync(DeleteAt(seen.Id, seen.Version));
        var read = await FactoryClient.GetAsync(ApiRoutes.Corrections.GetRecord(seen.Id));
        var feed = await FactoryClient.PullAsync();

        // Assert
        deleted.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        read.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        feed.Tombstones.ShouldContain(t => t.EntityType == "Correction" && t.Id == seen.Id);
    }

    [Test]
    public async Task a_delete_with_no_version_removes_the_correction_after_an_edit()
    {
        // Arrange
        var seen = await SignInWithCorrectionAsync();
        await FactoryClient.PutAsJsonAsync(ApiRoutes.Corrections.GetRecord(seen.Id),
            new { canonicalTerm = "Tallinn", mishearings = WebEdit, version = seen.Version });

        // Act
        var deleted = await FactoryClient.DeleteAsync(ApiRoutes.Corrections.GetRecord(seen.Id));
        var read = await FactoryClient.GetAsync(ApiRoutes.Corrections.GetRecord(seen.Id));

        // Assert
        deleted.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        read.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private static string DeleteAt(string id, uint version) => $"{ApiRoutes.Corrections.GetRecord(id)}?ifVersion={version}";

    private async Task<CorrectionDto> SignInWithCorrectionAsync()
    {
        var (_, member) = await JournalScenario.AddPeopleAsync();
        FactoryClient.SignInAsAppUser(member.Identifier);
        var created = await FactoryClient.PostAsJsonAsync(ApiRoutes.Corrections.Root,
            new { canonicalTerm = "Tallinn", mishearings = Seen });
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await created.Content.ReadFromJsonAsync<CorrectionDto>())!;
    }
}
