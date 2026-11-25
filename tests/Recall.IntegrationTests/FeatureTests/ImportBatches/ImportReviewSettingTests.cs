namespace Recall.IntegrationTests.FeatureTests.ImportBatches;

using Recall.Server.Auth.Entities;
using Recall.Server.Domain.Users;
using Recall.Server.Domain.UserSettings.Dtos;
using Recall.Server.Domain.UserSettings.Features;
using Recall.Server.Exceptions;
using Shouldly;
using static ImportScenario;

public class ImportReviewSettingTests : TestBase
{
    [Test]
    [Property("Rule", "IMP-19")]
    public async Task the_import_review_is_on_for_a_new_user()
    {
        // Arrange
        var owner = await AddOwnerAsync();

        // Act
        var settings = await ScopeOf(owner).SendAsync(new GetUserSetting.Query());

        // Assert
        settings.RequireImportReview.ShouldBeTrue();
    }

    [Test]
    [Property("Rule", "IMP-19")]
    public async Task only_a_web_session_changes_the_import_review()
    {
        // Arrange
        var owner = await AddOwnerAsync();
        var native = new TestingServiceScope();
        native.SetAppSession(owner.Identifier, SessionKind.Native);
        var token = new TestingServiceScope();
        token.SetPersonalAccessToken(owner.Identifier, new TokenGrant([], JournalAccess.ReadWrite));
        var provider = new TestingServiceScope();
        provider.SetUserWithDefaults(identifier: owner.Identifier);
        var off = new ImportReviewChangeDto { RequireImportReview = false, Version = 0 };

        // Act
        var refusals = new List<ForbiddenAccessException>();
        foreach (var caller in new[] { native, token, provider })
            refusals.Add(await Should.ThrowAsync<ForbiddenAccessException>(() => caller.SendAsync(new ChangeImportReview.Command(off))));
        var changed = await ScopeOf(owner).SendAsync(new ChangeImportReview.Command(off));
        var on = await ScopeOf(owner).SendAsync(new ChangeImportReview.Command(new ImportReviewChangeDto { RequireImportReview = true, Version = changed.Version }));

        // Assert
        refusals.Select(r => r.Message).ShouldBe(Enumerable.Repeat(ChangeImportReview.WebSessionMessage, 3));
        changed.RequireImportReview.ShouldBeFalse();
        on.RequireImportReview.ShouldBeTrue();
        on.Version.ShouldNotBe(changed.Version);
    }

    [Test]
    [Property("Rule", "IMP-19")]
    public async Task a_change_of_the_other_settings_does_not_change_the_import_review()
    {
        // Arrange
        var owner = await AddOwnerAsync();
        var off = await ScopeOf(owner).SendAsync(new ChangeImportReview.Command(new ImportReviewChangeDto { RequireImportReview = false, Version = 0 }));
        var native = new TestingServiceScope();
        native.SetAppSession(owner.Identifier, SessionKind.Native);

        // Act
        var changed = await native.SendAsync(new ChangeUserSetting.Command(new UserSettingChangeDto
        {
            TimeZoneMode = "Fixed", TimeZone = "Europe/Paris", LocationCaptureEnabled = true, RequirePeopleProposalApproval = true, Version = off.Version,
        }));

        // Assert
        changed.TimeZone.ShouldBe("Europe/Paris");
        changed.RequireImportReview.ShouldBeFalse();
        (await native.SendAsync(new GetUserSetting.Query())).RequireImportReview.ShouldBeFalse();
    }

    [Test]
    [Property("Rule", "IMP-19")]
    public async Task a_change_from_another_version_gets_409_unless_the_settings_hold_the_value()
    {
        // Arrange
        var owner = await AddOwnerAsync();
        var off = await ScopeOf(owner).SendAsync(new ChangeImportReview.Command(new ImportReviewChangeDto { RequireImportReview = false, Version = 0 }));

        // Act
        var replay = await ScopeOf(owner).SendAsync(new ChangeImportReview.Command(new ImportReviewChangeDto { RequireImportReview = false, Version = 0 }));
        var stale = () => ScopeOf(owner).SendAsync(new ChangeImportReview.Command(new ImportReviewChangeDto { RequireImportReview = true, Version = 0 }));

        // Assert
        replay.ShouldBe(off);
        await stale.ShouldThrowAsync<StaleVersionException>();
        (await ScopeOf(owner).SendAsync(new GetUserSetting.Query())).RequireImportReview.ShouldBeFalse();
    }
}
