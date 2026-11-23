namespace Recall.FunctionalTests.FunctionalTests.People;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ModelProviders;
using Recall.Server.Domain.People.Dtos;
using Recall.Server.Domain.Sessions.Dtos;
using Sessions;
using Shouldly;
using TestUtilities;

/// <summary>The details of a Person tell two People of one label apart. A mention shows only the label.</summary>
public class PersonDetailsTests : TestBase
{
    private const string StaleVersionType = "urn:recall:problem:stale-version";

    [Test]
    [Property("Rule", "SES-55")]
    public async Task an_add_keeps_the_details_and_the_list_and_the_sync_feed_carry_them()
    {
        // Arrange
        await SignInAsync();

        // Act
        var added = await AddAsync(new { label = "Jane", fullName = "  Jane   Park ", notes = " Sam's teacher\nmet at school " });
        var list = await FactoryClient.GetFromJsonAsync<List<PersonDto>>(ApiRoutes.People.Root);
        var feed = await FactoryClient.PullAsync();

        // Assert
        added.Label.ShouldBe("Jane");
        added.FullName.ShouldBe("Jane Park");
        added.Notes.ShouldBe("Sam's teacher\nmet at school");
        list.ShouldNotBeNull().ShouldHaveSingleItem().FullName.ShouldBe("Jane Park");
        var row = feed.People.ShouldHaveSingleItem();
        row.FullName.ShouldBe("Jane Park");
        row.Notes.ShouldBe("Sam's teacher\nmet at school");
    }

    [Test]
    [Property("Rule", "SES-55")]
    public async Task an_edit_of_the_details_keeps_the_label_and_the_mentions()
    {
        // Arrange
        await SignInAsync();
        var person = await AddAsync(new { label = "Jane" });
        var session = await FactoryClient.AddSessionAsync();
        var mention = "{\"type\":\"doc\",\"content\":[{\"type\":\"paragraph\",\"content\":[{\"type\":\"text\",\"text\":\"Coffee with \"},"
            + "{\"type\":\"mention\",\"attrs\":{\"personId\":\"" + person.Id + "\",\"label\":\"Jane\"}}]}]}";
        await FactoryClient.SaveDraftAsync(session.Id, mention, 0);
        var before = (await FactoryClient.GetFromJsonAsync<SessionDto>(ApiRoutes.Sessions.GetRecord(session.Id)))!;

        // Act
        var described = await FactoryClient.PutAsJsonAsync(ApiRoutes.People.Details(person.Id), new { fullName = "Jane Park", notes = "Sam's teacher", version = person.Version });
        var cleared = await FactoryClient.PutAsJsonAsync(ApiRoutes.People.Details(person.Id), new { fullName = "Jane Park", notes = "   ", version = person.Version + 1 });
        var after = (await FactoryClient.GetFromJsonAsync<SessionDto>(ApiRoutes.Sessions.GetRecord(session.Id)))!;

        // Assert
        described.StatusCode.ShouldBe(HttpStatusCode.OK, await described.Content.ReadAsStringAsync());
        var stored = (await cleared.Content.ReadFromJsonAsync<PersonDto>())!;
        stored.Label.ShouldBe("Jane");
        stored.FullName.ShouldBe("Jane Park");
        stored.Notes.ShouldBeNull();
        after.RawText.ShouldBe(before.RawText);
        after.RawLatestRevision.ShouldBe(before.RawLatestRevision);
        after.Version.ShouldBe(before.Version);
        after.PersonIds.ShouldBe([person.Id]);
    }

    [Test]
    [Property("Rule", "SES-55")]
    public async Task a_rename_keeps_the_details()
    {
        // Arrange
        await SignInAsync();
        var person = await AddAsync(new { label = "Jane", fullName = "Jane Park", notes = "Sam's teacher" });

        // Act
        var renamed = await FactoryClient.PutAsJsonAsync(ApiRoutes.People.GetRecord(person.Id), new { label = "Janie" });

        // Assert
        var stored = (await renamed.Content.ReadFromJsonAsync<PersonDto>())!;
        stored.Label.ShouldBe("Janie");
        stored.FullName.ShouldBe("Jane Park");
        stored.Notes.ShouldBe("Sam's teacher");
    }

    [Test]
    [Property("Rule", "SES-55")]
    public async Task a_detail_edit_at_a_version_that_a_rename_moved_gives_409_and_keeps_the_details()
    {
        // Arrange
        await SignInAsync();
        var seen = await AddAsync(new { label = "Jane", fullName = "Jane Park" });
        await FactoryClient.PutAsJsonAsync(ApiRoutes.People.GetRecord(seen.Id), new { label = "Janie" });

        // Act
        var refused = await FactoryClient.PutAsJsonAsync(ApiRoutes.People.Details(seen.Id), new { fullName = "Jane Doe", version = seen.Version });

        // Assert
        refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("type").GetString().ShouldBe(StaleVersionType);
        (await FactoryClient.GetFromJsonAsync<PersonDto>(ApiRoutes.People.GetRecord(seen.Id)))!.FullName.ShouldBe("Jane Park");
    }

    [Test]
    [Property("Rule", "SES-55")]
    public async Task the_details_do_not_reach_the_model_provider()
    {
        // Arrange
        await using var scenario = await CleanupScenario.StartAsync(FactoryClient);
        await AddAsync(new { label = "Jane", fullName = "Juniper Parkhurst", notes = "Teaches Sam at Willowbrook" });
        var started = await scenario.SessionWithRunningCleanupAsync("Coffee with Jane today");

        // Act
        await scenario.RunJobAsync(started.Id);

        // Assert
        var prompt = scenario.Fake.RequestBodies.ShouldHaveSingleItem();
        prompt.ShouldContain("Coffee with Jane today");
        prompt.ShouldNotContain("Juniper");
        prompt.ShouldNotContain("Parkhurst");
        prompt.ShouldNotContain("Willowbrook");
    }

    private async Task SignInAsync()
    {
        var (_, member) = await JournalScenario.AddPeopleAsync();
        FactoryClient.SignInAsAppUser(member.Identifier);
    }

    private async Task<PersonDto> AddAsync(object body)
    {
        var answer = await FactoryClient.PostAsJsonAsync(ApiRoutes.People.Root, body);
        answer.StatusCode.ShouldBe(HttpStatusCode.Created, await answer.Content.ReadAsStringAsync());
        return (await answer.Content.ReadFromJsonAsync<PersonDto>()).ShouldNotBeNull();
    }
}
