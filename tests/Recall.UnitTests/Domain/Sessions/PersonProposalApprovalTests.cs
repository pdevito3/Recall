namespace Recall.UnitTests.Domain.Sessions;

using Recall.Server.Domain.People;
using Recall.Server.Domain.Sessions;
using Shouldly;

public class PersonProposalApprovalTests
{
    private static Person Jane() => Person.Create("ten_1", "usr_1", "Jane");

    [Test]
    [Property("Rule", "SES-56")]
    public void an_approve_with_no_choice_between_two_janes_asks_which()
    {
        // Arrange
        var (park, doe) = (Jane(), Jane());
        var proposal = new PersonProposal("Jane", [park.Id, doe.Id]);

        // Act
        var approval = PersonProposalApproval.Decide(proposal, chosen: null, createNew: false, [park, doe]);

        // Assert
        approval.ShouldBeOfType<PersonProposalApproval.AskWhich>().PersonIds.ShouldBe([park.Id, doe.Id], ignoreOrder: true);
    }

    [Test]
    [Property("Rule", "SES-56")]
    public void a_second_jane_added_after_the_cleanup_also_asks_which()
    {
        // Arrange
        var (park, doe) = (Jane(), Jane());
        var proposal = new PersonProposal("Jane", [park.Id]);

        // Act
        var approval = PersonProposalApproval.Decide(proposal, chosen: null, createNew: false, [park, doe]);

        // Assert
        approval.ShouldBeOfType<PersonProposalApproval.AskWhich>().PersonIds.Count.ShouldBe(2);
    }

    [Test]
    [Property("Rule", "SES-56")]
    public void a_choice_binds_the_chosen_jane_and_a_new_person_is_a_request()
    {
        // Arrange
        var (park, doe) = (Jane(), Jane());
        var proposal = new PersonProposal("Jane", [park.Id, doe.Id]);

        // Act
        var bound = PersonProposalApproval.Decide(proposal, doe, createNew: false, [park, doe]);
        var added = PersonProposalApproval.Decide(proposal, chosen: null, createNew: true, []);

        // Assert
        bound.ShouldBeOfType<PersonProposalApproval.Bind>().Person.ShouldBe(doe);
        added.ShouldBeOfType<PersonProposalApproval.Create>().Kind.ShouldBe(NewPersonKind.AskedFor);
    }

    [Test]
    [Property("Rule", "SES-56")]
    public void one_live_match_binds_also_after_a_rename_and_none_adds_the_proposed_name()
    {
        // Arrange
        var renamed = Jane();
        renamed.Rename("Janie");
        var deleted = new PersonProposal("Jane", ["per_gone"]);

        // Act
        var bound = PersonProposalApproval.Decide(new PersonProposal("Jane", [renamed.Id]), chosen: null, createNew: false, [renamed]);
        var added = PersonProposalApproval.Decide(deleted, chosen: null, createNew: false, [renamed]);

        // Assert
        bound.ShouldBeOfType<PersonProposalApproval.Bind>().Person.ShouldBe(renamed);
        added.ShouldBeOfType<PersonProposalApproval.Create>().Kind.ShouldBe(NewPersonKind.ProposedName);
    }
}
