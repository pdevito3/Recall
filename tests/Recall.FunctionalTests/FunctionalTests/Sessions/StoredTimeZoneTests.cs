namespace Recall.FunctionalTests.FunctionalTests.Sessions;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Recall.Server.Domain.Sessions.Dtos;
using Serilog.Events;
using Shouldly;
using TestUtilities;

// A restore onto a host with other tzdata stores a zone that the host does not know. No route can store one.
public class StoredTimeZoneTests : TestBase
{
    private const string NotOnThisHost = "Mars/Olympus_Mons";

    private static readonly DateTimeOffset LateAtNight = new(2026, 6, 2, 2, 0, 0, TimeSpan.Zero);

    private async Task<string> SignInWithStoredZoneAsync(string zone = NotOnThisHost)
    {
        var (_, member) = await JournalScenario.AddPeopleAsync();
        FactoryClient.SignInAsAppUser(member.Identifier);
        var change = await FactoryClient.PutAsJsonAsync(ApiRoutes.UserSettings.Root, new { timeZone = "UTC", locationCaptureEnabled = false, requirePeopleProposalApproval = true, version = 0 });
        change.StatusCode.ShouldBe(HttpStatusCode.OK);
        await JournalScenario.ExecuteOutOfBandAsync("UPDATE user_settings SET time_zone = @zone WHERE user_id = @id", ("@zone", zone), ("@id", member.Id));
        return member.Id;
    }

    [Test]
    [Property("Rule", "SES-3")]
    [Arguments(NotOnThisHost)]
    [Arguments("america/chicago")]
    [Arguments(" ")]
    public async Task each_journal_read_and_a_create_work_and_the_day_comes_from_utc(string zone)
    {
        // Arrange
        var userId = await SignInWithStoredZoneAsync("UTC");
        var session = await FactoryClient.AddSessionAsync(LateAtNight);
        await JournalScenario.ExecuteOutOfBandAsync("UPDATE user_settings SET time_zone = @zone WHERE user_id = @id", ("@zone", zone), ("@id", userId));

        // Act
        var detail = await FactoryClient.GetAsync(ApiRoutes.Sessions.GetRecord(session.Id));
        var list = await FactoryClient.GetAsync(ApiRoutes.Sessions.Root);
        var feed = await FactoryClient.GetAsync(ApiRoutes.Sync.Changes);
        var created = await FactoryClient.PostAsJsonAsync(ApiRoutes.Sessions.Root, new { clientSessionId = Guid.NewGuid().ToString(), startedAt = LateAtNight });
        var metadata = await FactoryClient.PutAsJsonAsync(ApiRoutes.Sessions.Metadata(session.Id), new { activity = "Walking", metadataVersion = 0 });

        // Assert
        detail.StatusCode.ShouldBe(HttpStatusCode.OK);
        list.StatusCode.ShouldBe(HttpStatusCode.OK);
        feed.StatusCode.ShouldBe(HttpStatusCode.OK);
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        metadata.StatusCode.ShouldBe(HttpStatusCode.OK);
        var utcDay = new DateOnly(2026, 6, 2);
        (await detail.Content.ReadFromJsonAsync<SessionDto>())!.JournalingDay.ShouldBe(utcDay);
        (await list.Content.ReadFromJsonAsync<SessionPageDto>())!.Items.ShouldAllBe(item => item.JournalingDay == utcDay);
        (await created.Content.ReadFromJsonAsync<SessionDto>())!.JournalingDay.ShouldBe(utcDay);
    }

    [Test]
    [Property("Rule", "SES-3")]
    public async Task a_reader_logs_one_warning_with_no_zone_in_it()
    {
        // Arrange
        var userId = await SignInWithStoredZoneAsync();

        // Act
        await FactoryClient.GetAsync(ApiRoutes.Sessions.Root);
        await FactoryClient.GetAsync(ApiRoutes.Sessions.Root);
        await FactoryClient.GetAsync(ApiRoutes.Sync.Changes);
        await FactoryClient.AddSessionAsync();

        // Assert
        var logs = TestingWebApplicationFactory.Instance.CapturedLogs;
        logs.Holding(userId).Where(e => e.Level == LogEventLevel.Warning).ShouldHaveSingleItem();
        logs.Holding(NotOnThisHost).ShouldBeEmpty();
    }

    [Test]
    [Property("Rule", "SES-3")]
    public async Task the_settings_show_the_stored_zone_and_that_the_server_does_not_know_it()
    {
        // Arrange
        await SignInWithStoredZoneAsync();

        // Act
        var settings = await FactoryClient.GetAsync(ApiRoutes.UserSettings.Root);
        var page = await FactoryClient.GetAsync(ApiRoutes.Sync.Changes);

        // Assert
        using var read = JsonDocument.Parse(await settings.Content.ReadAsStringAsync());
        read.RootElement.GetProperty("timeZone").GetString().ShouldBe(NotOnThisHost);
        read.RootElement.GetProperty("timeZoneIsKnown").GetBoolean().ShouldBeFalse();
        using var pulled = JsonDocument.Parse(await page.Content.ReadAsStringAsync());
        pulled.RootElement.GetProperty("settings").GetProperty("timeZoneIsKnown").GetBoolean().ShouldBeFalse();
    }

    [Test]
    [Property("Rule", "SES-3")]
    public async Task a_person_corrects_the_zone_and_can_change_other_settings_before_that()
    {
        // Arrange
        await SignInWithStoredZoneAsync();

        // Act
        var echo = await FactoryClient.PutAsJsonAsync(ApiRoutes.UserSettings.Root, new { timeZone = NotOnThisHost, locationCaptureEnabled = true, requirePeopleProposalApproval = true, version = 1 });
        var otherUnknown = await FactoryClient.PutAsJsonAsync(ApiRoutes.UserSettings.Root, new { timeZone = "Mars/Elsewhere", locationCaptureEnabled = true, requirePeopleProposalApproval = true, version = 2 });
        var corrected = await FactoryClient.PutAsJsonAsync(ApiRoutes.UserSettings.Root, new { timeZone = "America/Chicago", locationCaptureEnabled = true, requirePeopleProposalApproval = true, version = 2 });

        // Assert
        echo.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var echoed = JsonDocument.Parse(await echo.Content.ReadAsStringAsync());
        echoed.RootElement.GetProperty("locationCaptureEnabled").GetBoolean().ShouldBeTrue();
        otherUnknown.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        using var read = JsonDocument.Parse(await corrected.Content.ReadAsStringAsync());
        read.RootElement.GetProperty("timeZone").GetString().ShouldBe("America/Chicago");
        read.RootElement.GetProperty("timeZoneIsKnown").GetBoolean().ShouldBeTrue();
        var session = await FactoryClient.AddSessionAsync(LateAtNight);
        session.JournalingDay.ShouldBe(new DateOnly(2026, 6, 1));
    }

    [Test]
    [Property("Rule", "SES-17")]
    [Arguments("pageSize=0")]
    [Arguments("pageSize=nonsense")]
    [Arguments("cursor=nonsense")]
    public async Task a_feed_with_a_bad_parameter_gives_400_because_a_422_means_only_a_cursor_ahead(string query)
    {
        // Arrange
        await SignInWithStoredZoneAsync();

        // Act
        var feed = await FactoryClient.GetAsync($"{ApiRoutes.Sync.Changes}?{query}");

        // Assert
        feed.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Test]
    [Property("Rule", "SES-17")]
    public async Task a_feed_with_a_stored_zone_that_the_host_does_not_know_gives_no_422()
    {
        // Arrange
        await SignInWithStoredZoneAsync();
        await FactoryClient.AddSessionAsync();

        // Act
        var feed = await FactoryClient.GetAsync(ApiRoutes.Sync.Changes);

        // Assert
        feed.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
