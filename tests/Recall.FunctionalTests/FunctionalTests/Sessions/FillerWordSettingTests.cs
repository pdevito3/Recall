namespace Recall.FunctionalTests.FunctionalTests.Sessions;

using System.Net;
using System.Net.Http.Json;
using Recall.Server.Domain.UserSettings.Dtos;
using Shouldly;
using TestUtilities;

public class FillerWordSettingTests : TestBase
{
    [Test]
    [Property("Rule", "SES-50")]
    public async Task a_new_user_has_the_removal_on_and_a_save_without_the_field_keeps_the_stored_value()
    {
        // Arrange
        var (_, member) = await JournalScenario.AddPeopleAsync();
        FactoryClient.SignInAsAppUser(member.Identifier);
        var fresh = await FactoryClient.GetFromJsonAsync<UserSettingDto>(ApiRoutes.UserSettings.Root);
        var off = await FactoryClient.PutAsJsonAsync(ApiRoutes.UserSettings.Root, new { timeZoneMode = "FollowDevice", locationCaptureEnabled = false, requirePeopleProposalApproval = true, removeFillerWords = false, version = 0 });
        off.StatusCode.ShouldBe(HttpStatusCode.OK, await off.Content.ReadAsStringAsync());

        // Act
        var oldClientBody = new { timeZoneMode = "FollowDevice", locationCaptureEnabled = true, requirePeopleProposalApproval = true, version = 1 };
        var change = await FactoryClient.PutAsJsonAsync(ApiRoutes.UserSettings.Root, oldClientBody);
        var resentAfterALostReply = await FactoryClient.PutAsJsonAsync(ApiRoutes.UserSettings.Root, oldClientBody);
        var stored = await FactoryClient.GetFromJsonAsync<UserSettingDto>(ApiRoutes.UserSettings.Root);

        // Assert
        fresh!.RemoveFillerWords.ShouldBeTrue();
        change.StatusCode.ShouldBe(HttpStatusCode.OK, await change.Content.ReadAsStringAsync());
        resentAfterALostReply.StatusCode.ShouldBe(HttpStatusCode.OK, await resentAfterALostReply.Content.ReadAsStringAsync());
        stored!.RemoveFillerWords.ShouldBeFalse();
        stored.LocationCaptureEnabled.ShouldBeTrue();
        stored.Version.ShouldBe(2u);
    }

    [Test]
    [Property("Rule", "SES-50")]
    public async Task a_save_with_the_field_null_keeps_the_stored_value()
    {
        // Arrange
        var (_, member) = await JournalScenario.AddPeopleAsync();
        FactoryClient.SignInAsAppUser(member.Identifier);
        var off = await FactoryClient.PutAsJsonAsync(ApiRoutes.UserSettings.Root, new { timeZoneMode = "FollowDevice", locationCaptureEnabled = false, requirePeopleProposalApproval = true, removeFillerWords = false, version = 0 });
        off.StatusCode.ShouldBe(HttpStatusCode.OK, await off.Content.ReadAsStringAsync());

        // Act
        var change = await FactoryClient.PutAsJsonAsync(ApiRoutes.UserSettings.Root, new { timeZoneMode = "FollowDevice", locationCaptureEnabled = true, requirePeopleProposalApproval = true, removeFillerWords = (bool?)null, version = 1 });
        var stored = await FactoryClient.GetFromJsonAsync<UserSettingDto>(ApiRoutes.UserSettings.Root);

        // Assert
        change.StatusCode.ShouldBe(HttpStatusCode.OK, await change.Content.ReadAsStringAsync());
        stored!.RemoveFillerWords.ShouldBeFalse();
        stored.LocationCaptureEnabled.ShouldBeTrue();
    }
}
