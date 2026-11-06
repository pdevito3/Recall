namespace Recall.FunctionalTests.FunctionalTests.Sessions;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Shouldly;
using TestUtilities;

public class BaseVersionTests : TestBase
{
    private const string StaleVersionType = "urn:recall:problem:stale-version";

    private static readonly string[] Food = ["Food"];
    private static readonly string[] FoodAndWork = ["Food", "Work"];
    private static readonly string[] Travel = ["Travel"];

    [Test]
    [Property("Rule", "SES-31")]
    public async Task a_stale_settings_save_is_refused_with_a_reason_and_the_next_session_stores_no_location()
    {
        // Arrange
        await SignInAsync();
        var defaults = await SettingsAsync();
        var on = await SaveSettingsAsync(defaults, capture: true, zone: "UTC");
        on.StatusCode.ShouldBe(HttpStatusCode.OK);
        var staleOnTablet = await SettingsAsync();
        (await SaveSettingsAsync(await SettingsAsync(), capture: false, zone: "UTC")).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Act
        var refused = await SaveSettingsAsync(staleOnTablet, capture: true, zone: "Europe/Tallinn");
        var problem = await refused.Content.ReadFromJsonAsync<JsonElement>();
        var stored = await SettingsAsync();
        var next = await AddSessionWithLocationAsync();

        // Assert
        refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        problem.GetProperty("type").GetString().ShouldBe(StaleVersionType);
        problem.GetProperty("detail").GetString().ShouldNotBeNullOrWhiteSpace();
        problem.GetProperty("errors").TryGetProperty("version", out _).ShouldBeTrue();
        stored.GetProperty("locationCaptureEnabled").GetBoolean().ShouldBeFalse();
        stored.GetProperty("timeZone").GetString().ShouldBe("UTC");
        next.GetProperty("latitude").ValueKind.ShouldBe(JsonValueKind.Null);
        next.GetProperty("longitude").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Test]
    [Property("Rule", "SES-31")]
    public async Task a_settings_save_from_the_version_of_the_defaults_makes_the_row_and_moves_the_version()
    {
        // Arrange
        await SignInAsync();
        var defaults = await SettingsAsync();

        // Act
        var saved = await SaveSettingsAsync(defaults, capture: true, zone: "UTC");
        var stored = await SettingsAsync();

        // Assert
        defaults.GetProperty("version").GetUInt32().ShouldBe(0u);
        saved.StatusCode.ShouldBe(HttpStatusCode.OK);
        stored.GetProperty("locationCaptureEnabled").GetBoolean().ShouldBeTrue();
        stored.GetProperty("version").GetUInt32().ShouldBe(1u);
    }

    [Test]
    [Property("Rule", "SES-32")]
    public async Task a_stale_metadata_save_is_refused_with_a_reason_and_the_topic_stays()
    {
        // Arrange
        await SignInAsync();
        var session = await AddSessionAsync();
        var first = await SaveMetadataAsync(session, "None", Food);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        var staleOnTablet = await SessionAsync(session);
        (await SaveMetadataAsync(await SessionAsync(session), "None", FoodAndWork)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Act
        var refused = await SaveMetadataAsync(staleOnTablet, "Walking", Food);
        var problem = await refused.Content.ReadFromJsonAsync<JsonElement>();
        var stored = await SessionAsync(session);

        // Assert
        refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        problem.GetProperty("type").GetString().ShouldBe(StaleVersionType);
        problem.GetProperty("detail").GetString().ShouldNotBeNullOrWhiteSpace();
        problem.GetProperty("errors").TryGetProperty("metadataVersion", out _).ShouldBeTrue();
        TopicsOf(stored).ShouldBe(FoodAndWork);
        stored.GetProperty("activity").GetString().ShouldBe("None");
    }

    [Test]
    [Property("Rule", "SES-32")]
    public async Task a_refused_metadata_save_keeps_the_words()
    {
        // Arrange
        await SignInAsync();
        var session = await AddSessionAsync();
        var staleOnTablet = await SessionAsync(session);
        (await SaveMetadataAsync(session, "None", Food)).StatusCode.ShouldBe(HttpStatusCode.OK);
        const string words = "words that the person said";
        (await FactoryClient.SaveDraftAsync(IdOf(session), words, 0)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Act
        var refused = await SaveMetadataAsync(staleOnTablet, "Walking", Travel);
        var stored = await SessionAsync(session);

        // Assert
        refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        stored.GetProperty("rawText").GetString().ShouldBe(words);
        stored.GetProperty("rawDraftRevision").GetInt32().ShouldBe(1);
    }

    [Test]
    [Property("Rule", "SES-32")]
    public async Task a_draft_save_does_not_refuse_a_metadata_save()
    {
        // Arrange
        await SignInAsync();
        var session = await AddSessionAsync();
        var read = await SessionAsync(session);
        (await FactoryClient.SaveDraftAsync(IdOf(session), "words", 0)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Act
        var saved = await SaveMetadataAsync(read, "Walking", Food);
        var stored = await SessionAsync(session);

        // Assert
        saved.StatusCode.ShouldBe(HttpStatusCode.OK);
        TopicsOf(stored).ShouldBe(Food);
        stored.GetProperty("rawText").GetString().ShouldBe("words");
        stored.GetProperty("version").GetUInt32().ShouldNotBe(read.GetProperty("version").GetUInt32());
    }

    [Test]
    [Property("Rule", "SES-32")]
    public async Task the_timeline_and_the_feed_show_the_metadata_version_that_a_save_sends()
    {
        // Arrange
        await SignInAsync();
        var session = await AddSessionAsync();
        (await SaveMetadataAsync(session, "None", Food)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Act
        var page = await FactoryClient.GetFromJsonAsync<JsonElement>(ApiRoutes.Sessions.Root);
        var feed = await FactoryClient.GetFromJsonAsync<JsonElement>(ApiRoutes.Sync.Changes);

        // Assert
        page.GetProperty("items")[0].GetProperty("metadataVersion").GetUInt32().ShouldBe(1u);
        feed.GetProperty("sessions")[0].GetProperty("metadataVersion").GetUInt32().ShouldBe(1u);
    }

    [Test]
    [Property("Rule", "SES-33")]
    public async Task a_settings_save_with_no_version_gives_400_and_changes_nothing()
    {
        // Arrange
        await SignInAsync();
        (await SaveSettingsAsync(await SettingsAsync(), capture: false, zone: "UTC")).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Act
        var change = await FactoryClient.PutAsJsonAsync(ApiRoutes.UserSettings.Root, new { timeZone = "UTC", locationCaptureEnabled = true, requirePeopleProposalApproval = true, savedAt = DateTimeOffset.UtcNow });
        var stored = await SettingsAsync();

        // Assert
        change.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        stored.GetProperty("locationCaptureEnabled").GetBoolean().ShouldBeFalse();
    }

    [Test]
    [Property("Rule", "SES-33")]
    public async Task a_metadata_save_with_no_metadata_version_gives_400_and_changes_nothing()
    {
        // Arrange
        await SignInAsync();
        var session = await AddSessionAsync();
        (await SaveMetadataAsync(session, "None", Food)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Act
        var change = await FactoryClient.PutAsJsonAsync(ApiRoutes.Sessions.Metadata(IdOf(session)), new { userTopics = Travel, savedAt = DateTimeOffset.UtcNow });
        var stored = await SessionAsync(session);

        // Assert
        change.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        TopicsOf(stored).ShouldBe(Food);
    }

    [Test]
    [Property("Rule", "SES-34")]
    public async Task a_retry_of_a_settings_save_does_nothing_and_gives_the_stored_row()
    {
        // Arrange
        await SignInAsync();
        var read = await SettingsAsync();
        (await SaveSettingsAsync(read, capture: true, zone: "UTC")).StatusCode.ShouldBe(HttpStatusCode.OK);
        var afterFirstTry = await SettingsAsync();

        // Act
        var retry = await SaveSettingsAsync(read, capture: true, zone: "UTC");
        var reply = await retry.Content.ReadFromJsonAsync<JsonElement>();
        var stored = await SettingsAsync();

        // Assert
        retry.StatusCode.ShouldBe(HttpStatusCode.OK);
        reply.GetProperty("locationCaptureEnabled").GetBoolean().ShouldBeTrue();
        reply.GetProperty("version").GetUInt32().ShouldBe(afterFirstTry.GetProperty("version").GetUInt32());
        stored.GetProperty("version").GetUInt32().ShouldBe(afterFirstTry.GetProperty("version").GetUInt32());
    }

    [Test]
    [Property("Rule", "SES-34")]
    public async Task a_retry_of_a_metadata_save_does_nothing_and_gives_the_stored_session()
    {
        // Arrange
        await SignInAsync();
        var session = await AddSessionAsync();
        (await SaveMetadataAsync(session, "Walking", Food)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var afterFirstTry = await SessionAsync(session);

        // Act
        var retry = await SaveMetadataAsync(session, "Walking", Food);
        var reply = await retry.Content.ReadFromJsonAsync<JsonElement>();

        // Assert
        retry.StatusCode.ShouldBe(HttpStatusCode.OK);
        reply.GetProperty("version").GetUInt32().ShouldBe(afterFirstTry.GetProperty("version").GetUInt32());
        reply.GetProperty("metadataVersion").GetUInt32().ShouldBe(1u);
    }

    [Test]
    [Property("Rule", "SES-34")]
    public async Task a_retry_after_a_newer_settings_save_is_refused_and_does_not_turn_capture_on()
    {
        // Arrange
        await SignInAsync();
        var read = await SettingsAsync();
        (await SaveSettingsAsync(read, capture: true, zone: "UTC")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await SaveSettingsAsync(await SettingsAsync(), capture: false, zone: "UTC")).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Act
        var retry = await SaveSettingsAsync(read, capture: true, zone: "UTC");
        var stored = await SettingsAsync();

        // Assert
        retry.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        stored.GetProperty("locationCaptureEnabled").GetBoolean().ShouldBeFalse();
    }

    [Test]
    [Property("Rule", "SES-34")]
    public async Task a_retry_after_a_newer_metadata_save_is_refused_and_does_not_drop_the_topic()
    {
        // Arrange
        await SignInAsync();
        var session = await AddSessionAsync();
        (await SaveMetadataAsync(session, "None", Food)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await SaveMetadataAsync(await SessionAsync(session), "None", FoodAndWork)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Act
        var retry = await SaveMetadataAsync(session, "None", Food);
        var stored = await SessionAsync(session);

        // Assert
        retry.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        TopicsOf(stored).ShouldBe(FoodAndWork);
    }

    [Test]
    [Property("Rule", "SES-35")]
    public async Task a_settings_save_from_the_current_version_applies_with_a_time_that_is_not_newer()
    {
        // Arrange
        await SignInAsync();
        (await SaveSettingsAsync(await SettingsAsync(), capture: true, zone: "UTC")).StatusCode.ShouldBe(HttpStatusCode.OK);
        var current = await SettingsAsync();

        // Act
        var off = await FactoryClient.PutAsJsonAsync(ApiRoutes.UserSettings.Root, new { timeZone = "UTC", locationCaptureEnabled = false, requirePeopleProposalApproval = true, version = current.GetProperty("version").GetUInt32(), savedAt = DateTimeOffset.UtcNow.AddDays(-1) });
        var stored = await SettingsAsync();

        // Assert
        off.StatusCode.ShouldBe(HttpStatusCode.OK);
        stored.GetProperty("locationCaptureEnabled").GetBoolean().ShouldBeFalse();
    }

    [Test]
    [Property("Rule", "SES-35")]
    public async Task a_metadata_save_from_the_current_version_applies_with_a_time_that_is_not_newer()
    {
        // Arrange
        await SignInAsync();
        var session = await AddSessionAsync();
        (await SaveMetadataAsync(session, "None", Food)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var current = await SessionAsync(session);

        // Act
        var change = await FactoryClient.PutAsJsonAsync(ApiRoutes.Sessions.Metadata(IdOf(session)), new { userTopics = FoodAndWork, metadataVersion = current.GetProperty("metadataVersion").GetUInt32(), savedAt = DateTimeOffset.UtcNow.AddDays(-1) });
        var stored = await SessionAsync(session);

        // Assert
        change.StatusCode.ShouldBe(HttpStatusCode.OK);
        TopicsOf(stored).ShouldBe(FoodAndWork);
    }

    private async Task SignInAsync()
    {
        var (_, member) = await JournalScenario.AddPeopleAsync();
        FactoryClient.SignInAsAppUser(member.Identifier);
    }

    private Task<JsonElement> SettingsAsync() => FactoryClient.GetFromJsonAsync<JsonElement>(ApiRoutes.UserSettings.Root);

    private Task<HttpResponseMessage> SaveSettingsAsync(JsonElement read, bool capture, string zone)
        => FactoryClient.PutAsJsonAsync(ApiRoutes.UserSettings.Root, new
        {
            timeZone = zone,
            locationCaptureEnabled = capture,
            requirePeopleProposalApproval = true,
            version = read.GetProperty("version").GetUInt32(),
            savedAt = DateTimeOffset.UtcNow,
        });

    private async Task<JsonElement> AddSessionAsync()
    {
        var created = await FactoryClient.PostAsJsonAsync(ApiRoutes.Sessions.Root, new { clientSessionId = Guid.NewGuid().ToString(), startedAt = JournalScenario.Morning });
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        return await created.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<JsonElement> AddSessionWithLocationAsync()
    {
        var created = await FactoryClient.PostAsJsonAsync(ApiRoutes.Sessions.Root, new { clientSessionId = Guid.NewGuid().ToString(), startedAt = JournalScenario.Morning, latitude = 1.5, longitude = 2.5 });
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        return await created.Content.ReadFromJsonAsync<JsonElement>();
    }

    private Task<JsonElement> SessionAsync(JsonElement session) => FactoryClient.GetFromJsonAsync<JsonElement>(ApiRoutes.Sessions.GetRecord(IdOf(session)));

    private Task<HttpResponseMessage> SaveMetadataAsync(JsonElement read, string activity, string[] topics)
        => FactoryClient.PutAsJsonAsync(ApiRoutes.Sessions.Metadata(IdOf(read)), new
        {
            activity,
            userTopics = topics,
            metadataVersion = read.TryGetProperty("metadataVersion", out var version) ? version.GetUInt32() : 0u,
            savedAt = DateTimeOffset.UtcNow,
        });

    private static string IdOf(JsonElement session) => session.GetProperty("id").GetString()!;

    private static string[] TopicsOf(JsonElement session)
        => session.GetProperty("topics").EnumerateArray().Select(topic => topic.GetProperty("name").GetString()!).ToArray();
}
