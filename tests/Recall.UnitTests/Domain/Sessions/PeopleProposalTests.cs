namespace Recall.UnitTests.Domain.Sessions;

using System.Text.Json.Nodes;
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
        var proposals = new[] { new PersonProposal("Sam", "per_sam"), new PersonProposal("Kim", "per_kim"), new PersonProposal("Lee", null) };
        session.CompleteCleanup(new CleanupOutcome(Tagged("per_sam", "Sam"), "A recap", [], [], proposals), session.Raw.DraftRevision, Noon);

        // Act
        var dto = session.ToSessionDto();

        // Assert
        dto.PeopleProposals.Select(proposal => proposal.Label).ShouldBe(["Kim", "Lee"]);
    }
}
