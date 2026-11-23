namespace Recall.UnitTests.Domain.Sync;

using Recall.Server.Domain.People;
using Recall.Server.Domain.Sessions;
using Recall.Server.Domain.Sync;
using Shouldly;

public class SessionsToSendAgainTests
{
    /// <summary>The held Session has one proposal of <paramref name="proposed"/>, or none when it is empty.</summary>
    [Test]
    [Property("Rule", "SES-56")]
    [Arguments("Jane", "Jane", false, true)]
    [Arguments("Jane", " JANE", false, true)]
    [Arguments("Jane", "Sam", false, false)]
    [Arguments("Jane", "Janie", true, true)]
    [Arguments("", "Jane", false, false)]
    public void a_held_session_goes_again_when_a_changed_person_joins_its_matches(string proposed, string changedLabel, bool changedIsAStoredMatch,
        bool sentAgain)
    {
        // Arrange
        var changed = Person.Create("ten_1", "usr_1", changedLabel);
        IReadOnlyList<PersonProposal> proposals = proposed.Length == 0
            ? []
            : [new PersonProposal(proposed, changedIsAStoredMatch ? [changed.Id] : ["per_other"])];

        // Act
        var ids = SessionsToSendAgain.Of([("ses_1", proposals), ("ses_2", [])], [changed]);

        // Assert
        ids.ShouldBe(sentAgain ? ["ses_1"] : []);
    }
}
