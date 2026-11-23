namespace Recall.UnitTests.Domain.Sessions;

using Recall.Server.Domain.People;
using Recall.Server.Domain.Sessions;
using Shouldly;

public class PersonProposalMatchesTests
{
    /// <summary>
    /// <paramref name="people"/> is a list of <c>key=label</c>, <paramref name="stored"/> and the result are lists of keys. A stored
    /// key with no Person is a deleted match.
    /// </summary>
    [Test]
    [Property("Rule", "SES-56")]
    [Arguments("a=Jane,b=Jane", "b,a", "b,a")]
    [Arguments("a=Jane", "gone", "a")]
    [Arguments("a=Janie", "a", "a")]
    [Arguments("a=Jane,b=Jane", "a", "a,b")]
    [Arguments("a=Jane,b=Sam", "", "a")]
    [Arguments("a=jane ,b=JANE", "", "a,b")]
    [Arguments("a=Jane", "a", "a")]
    [Arguments("a=Sam", "", "")]
    public void the_matches_are_each_stored_match_that_exists_then_each_other_person_of_the_name(string people, string stored, string expected)
    {
        // Arrange
        var byKey = people.Split(',').Select(pair => pair.Split('=')).ToDictionary(pair => pair[0], pair => Person.Create("ten_1", "usr_1", pair[1]));
        var proposal = new PersonProposal("Jane", Keys(stored).Select(key => byKey.TryGetValue(key, out var person) ? person.Id : "per_" + key).ToList());

        // Act
        var matches = proposal.MatchesIn(byKey.Values.ToList());

        // Assert
        matches.ShouldBe(Keys(expected).Select(key => byKey[key]).ToList());
    }

    [Test]
    [Property("Rule", "SES-56")]
    public void a_proposal_is_settled_when_cleaned_tags_a_match_of_now_and_not_by_a_tag_of_another_person()
    {
        // Arrange
        var (park, doe) = (Person.Create("ten_1", "usr_1", "Jane"), Person.Create("ten_1", "usr_1", "Jane"));

        // Act
        var byTheLateJane = PersonProposal.IsSettledBy(new HashSet<string> { doe.Id }, [park, doe]);
        var byAnother = PersonProposal.IsSettledBy(new HashSet<string> { "per_sam" }, [park, doe]);

        // Assert
        byTheLateJane.ShouldBeTrue();
        byAnother.ShouldBeFalse();
    }

    private static string[] Keys(string list) => list.Split(',', StringSplitOptions.RemoveEmptyEntries);
}
