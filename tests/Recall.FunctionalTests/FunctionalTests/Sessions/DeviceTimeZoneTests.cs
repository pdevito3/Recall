namespace Recall.FunctionalTests.FunctionalTests.Sessions;

using System.Net;
using System.Net.Http.Json;
using Recall.Server.Domain.Sessions.Dtos;
using Recall.Server.Domain.UserSettings.Dtos;
using Shouldly;
using TestUtilities;

public class DeviceTimeZoneTests : TestBase
{
    private static readonly DateTimeOffset LateAtNight = new(2026, 6, 2, 2, 0, 0, TimeSpan.Zero);

    private async Task SignInAsync()
    {
        var (_, member) = await JournalScenario.AddPeopleAsync();
        FactoryClient.SignInAsAppUser(member.Identifier);
    }

    private Task<HttpResponseMessage> ReportAsync(string zone)
        => FactoryClient.PutAsJsonAsync(ApiRoutes.UserSettings.DeviceTimeZone, new { timeZone = zone });

    private Task<HttpResponseMessage> PickAsync(string zone, uint version)
        => FactoryClient.PutAsJsonAsync(ApiRoutes.UserSettings.Root, new { timeZoneMode = "Fixed", timeZone = zone, locationCaptureEnabled = false, requirePeopleProposalApproval = true, version });

    [Test]
    [Property("Rule", "SES-46")]
    public async Task a_new_user_follows_the_device_and_takes_each_zone_that_it_reports()
    {
        // Arrange
        await SignInAsync();
        var before = await FactoryClient.GetFromJsonAsync<UserSettingDto>(ApiRoutes.UserSettings.Root);

        // Act
        var first = await ReportAsync("America/Chicago");
        var second = await ReportAsync("Asia/Tokyo");
        var read = await FactoryClient.GetFromJsonAsync<UserSettingDto>(ApiRoutes.UserSettings.Root);

        // Assert
        (before!.TimeZoneMode, before.TimeZone).ShouldBe(("FollowDevice", null));
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await first.Content.ReadFromJsonAsync<UserSettingDto>())!.TimeZone.ShouldBe("America/Chicago");
        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        (read!.TimeZoneMode, read.TimeZone, read.Version).ShouldBe(("FollowDevice", "Asia/Tokyo", 2u));
    }

    [Test]
    [Property("Rule", "SES-46")]
    public async Task a_fixed_zone_holds_when_a_device_reports_another_and_the_version_stays()
    {
        // Arrange
        await SignInAsync();
        (await PickAsync("Europe/Paris", 0)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Act
        var report = await ReportAsync("America/Chicago");
        var read = await FactoryClient.GetFromJsonAsync<UserSettingDto>(ApiRoutes.UserSettings.Root);

        // Assert
        report.StatusCode.ShouldBe(HttpStatusCode.OK);
        (read!.TimeZoneMode, read.TimeZone, read.Version).ShouldBe(("Fixed", "Europe/Paris", 1u));
    }

    [Test]
    [Property("Rule", "SES-46")]
    public async Task a_report_of_the_zone_that_holds_changes_nothing()
    {
        // Arrange
        await SignInAsync();
        await ReportAsync("America/Chicago");

        // Act
        var again = await ReportAsync("America/Chicago");

        // Assert
        again.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await again.Content.ReadFromJsonAsync<UserSettingDto>())!.Version.ShouldBe(1u);
    }

    [Test]
    [Property("Rule", "SES-46")]
    [Arguments("Mars/Olympus_Mons", HttpStatusCode.UnprocessableEntity)]
    [Arguments("", HttpStatusCode.UnprocessableEntity)]
    public async Task a_report_of_a_zone_that_is_not_an_iana_zone_is_refused(string zone, HttpStatusCode status)
    {
        // Arrange
        await SignInAsync();

        // Act
        var report = await ReportAsync(zone);
        var read = await FactoryClient.GetFromJsonAsync<UserSettingDto>(ApiRoutes.UserSettings.Root);

        // Assert
        report.StatusCode.ShouldBe(status);
        read!.TimeZone.ShouldBeNull();
    }

    [Test]
    [Property("Rule", "SES-46")]
    public async Task a_report_with_no_zone_gives_400()
    {
        // Arrange
        await SignInAsync();

        // Act
        var report = await FactoryClient.PutAsJsonAsync(ApiRoutes.UserSettings.DeviceTimeZone, new { });

        // Assert
        report.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Test]
    [Property("Rule", "SES-46")]
    public async Task a_fixed_change_with_no_zone_gives_422()
    {
        // Arrange
        await SignInAsync();

        // Act
        var change = await FactoryClient.PutAsJsonAsync(ApiRoutes.UserSettings.Root, new { timeZoneMode = "Fixed", locationCaptureEnabled = false, requirePeopleProposalApproval = true, version = 0 });

        // Assert
        change.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Test]
    [Property("Rule", "SES-46")]
    public async Task the_feed_sends_the_settings_after_a_report()
    {
        // Arrange
        await SignInAsync();
        var start = await FactoryClient.PullAsync();

        // Act
        await ReportAsync("Asia/Tokyo");
        var page = await FactoryClient.PullAsync(start.Cursor);

        // Assert
        (page.Settings!.TimeZoneMode, page.Settings.TimeZone).ShouldBe(("FollowDevice", "Asia/Tokyo"));
    }

    [Test]
    [Property("Rule", "SES-3")]
    public async Task a_session_takes_the_zone_of_its_device_and_keeps_its_day_after_the_trip()
    {
        // Arrange
        await SignInAsync();
        await ReportAsync("America/Chicago");

        // Act
        var onTheTrip = await FactoryClient.PostAsJsonAsync(ApiRoutes.Sessions.Root, new { clientSessionId = Guid.NewGuid().ToString(), startedAt = LateAtNight, timeZone = "America/Chicago" });
        var session = (await onTheTrip.Content.ReadFromJsonAsync<SessionDto>())!;
        await ReportAsync("Asia/Tokyo");
        var read = await FactoryClient.GetFromJsonAsync<SessionDto>(ApiRoutes.Sessions.GetRecord(session.Id));
        var page = await FactoryClient.GetFromJsonAsync<SessionPageDto>(ApiRoutes.Sessions.Root);
        var feed = await FactoryClient.PullAsync();

        // Assert
        session.JournalingDay.ShouldBe(new DateOnly(2026, 6, 1));
        read!.JournalingDay.ShouldBe(new DateOnly(2026, 6, 1));
        page!.Items.ShouldHaveSingleItem().JournalingDay.ShouldBe(new DateOnly(2026, 6, 1));
        feed.Sessions.ShouldHaveSingleItem().JournalingDay.ShouldBe(new DateOnly(2026, 6, 1));
    }

    [Test]
    [Property("Rule", "SES-3")]
    public async Task a_fixed_zone_wins_over_the_zone_that_a_create_sends()
    {
        // Arrange
        await SignInAsync();
        (await PickAsync("Europe/Paris", 0)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Act
        var created = await FactoryClient.PostAsJsonAsync(ApiRoutes.Sessions.Root, new { clientSessionId = Guid.NewGuid().ToString(), startedAt = LateAtNight, timeZone = "America/Chicago" });

        // Assert
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await created.Content.ReadFromJsonAsync<SessionDto>())!.JournalingDay.ShouldBe(new DateOnly(2026, 6, 2));
    }

    [Test]
    [Property("Rule", "SES-3")]
    [Arguments(null)]
    [Arguments("Mars/Olympus_Mons")]
    public async Task a_create_with_no_zone_or_an_unknown_zone_takes_the_zone_of_the_settings(string? zone)
    {
        // Arrange
        await SignInAsync();
        await ReportAsync("Asia/Tokyo");

        // Act
        var created = await FactoryClient.PostAsJsonAsync(ApiRoutes.Sessions.Root, new { clientSessionId = Guid.NewGuid().ToString(), startedAt = LateAtNight.AddHours(20), timeZone = zone });

        // Assert
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await created.Content.ReadFromJsonAsync<SessionDto>())!.JournalingDay.ShouldBe(new DateOnly(2026, 6, 3));
    }
}
