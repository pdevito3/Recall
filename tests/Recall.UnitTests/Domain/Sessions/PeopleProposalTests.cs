namespace Recall.UnitTests.Domain.Sessions;

using System.Text.Json.Nodes;
using Recall.Server.Domain.People;
using Recall.Server.Domain.Sessions;
using Recall.Server.Domain.Sessions.Mappings;
using Recall.Server.Domain.TimeZones;
using Shouldly;

public class PeopleProposalTests
{
    private static readonly DateTimeOffset Noon = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private static string Tagged(string personId, string label)
        => new JsonObject
        {
            ["type"] = "doc",
            ["content"] = new JsonArray(new JsonObject
            {
                ["type"] = "paragraph",
                ["content"] = new JsonArray(
                    new JsonObject { ["type"] = "text", ["text"] = "Lunch with " },
                    new JsonObject { ["type"] = "mention", ["attrs"] = new JsonObject { ["personId"] = personId, ["label"] = label } }),
            }),
        }.ToJsonString();

    [Test]
    public void a_person_whom_cleaned_tags_is_not_offered_again()
    {
        // Arrange
        var session = Session.Create("ten_1", "usr_1", "device-1", Noon, null, TimeZoneId.Utc, Noon);
        session.SaveRaw(DraftSave.Of("raw words", 0, Noon));
        session.BeginCleanup();
        var (sam, kim) = (Named("Sam"), Named("Kim"));
        var proposals = new[] { new PersonProposal("Sam", [sam.Id]), new PersonProposal("Kim", [kim.Id]), new PersonProposal("Lee", []) };
        session.CompleteCleanup(new CleanupOutcome(Tagged(sam.Id, "Sam"), "A recap", [], [], proposals), session.Raw.DraftRevision, Noon);

        // Act
        var dto = session.ToSessionDto(ProposalPeople.Of([sam, kim]));

        // Assert
        dto.PeopleProposals.Select(proposal => proposal.Label).ShouldBe(["Kim", "Lee"]);
    }

    [Test]
    [Property("Rule", "SES-56")]
    public void a_proposal_of_two_people_shows_both_until_cleaned_tags_one_of_them()
    {
        // Arrange
        var session = Session.Create("ten_1", "usr_1", "device-1", Noon, null, TimeZoneId.Utc, Noon);
        session.SaveRaw(DraftSave.Of("raw words", 0, Noon));
        session.BeginCleanup();
        var (park, doe, sam, samLee) = (Named("Jane"), Named("Jane"), Named("Sam"), Named("Sam"));
        var proposals = new[] { new PersonProposal("Jane", [park.Id, doe.Id]), new PersonProposal("Sam", [sam.Id, samLee.Id]) };
        session.CompleteCleanup(new CleanupOutcome(Tagged(samLee.Id, "Sam"), "A recap", [], [], proposals), session.Raw.DraftRevision, Noon);

        // Act
        var dto = session.ToSessionDto(ProposalPeople.Of([park, doe, sam, samLee]));

        // Assert
        var jane = dto.PeopleProposals.ShouldHaveSingleItem();
        jane.Label.ShouldBe("Jane");
        jane.MatchedPersonIds.ShouldBe([park.Id, doe.Id]);
        jane.IsNew.ShouldBeFalse();
    }

    [Test]
    [Property("Rule", "SES-56")]
    public void a_proposal_lists_the_people_of_now_and_is_new_only_with_none()
    {
        // Arrange
        var session = Session.Create("ten_1", "usr_1", "device-1", Noon, null, TimeZoneId.Utc, Noon);
        session.SaveRaw(DraftSave.Of("raw words", 0, Noon));
        session.BeginCleanup();
        var (park, lateJane, lateLee) = (Named("Jane"), Named("Jane"), Named("Lee"));
        var proposals = new[] { new PersonProposal("Jane", [park.Id]), new PersonProposal("Lee", []), new PersonProposal("Kim", []) };
        session.CompleteCleanup(new CleanupOutcome(Tagged("per_sam", "Sam"), "A recap", [], [], proposals), session.Raw.DraftRevision, Noon);

        // Act
        var dto = session.ToSessionDto(ProposalPeople.Of([lateLee, lateJane, park]));

        // Assert
        dto.PeopleProposals.Select(proposal => (proposal.Label, string.Join(",", proposal.MatchedPersonIds), proposal.IsNew))
            .ShouldBe([("Jane", $"{park.Id},{lateJane.Id}", false), ("Lee", lateLee.Id, false), ("Kim", "", true)]);
    }

    private static Person Named(string label) => Person.Create("ten_1", "usr_1", label);
}
