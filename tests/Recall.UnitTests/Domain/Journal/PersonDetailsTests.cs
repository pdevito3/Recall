namespace Recall.UnitTests.Domain.Journal;

using Recall.Server.Domain.People;
using Recall.Server.Exceptions;
using Shouldly;

public class PersonDetailsTests
{
    [Test]
    [Property("Rule", "SES-55")]
    public void the_details_are_trimmed_and_a_blank_detail_is_none()
    {
        // Act
        var person = Person.Create("ten_1", "usr_1", "Jane", " Jane \t Park ", "   ");

        // Assert
        person.FullName.ShouldBe("Jane Park");
        person.Notes.ShouldBeNull();
    }

    [Test]
    [Property("Rule", "SES-55")]
    public void a_detail_edit_keeps_the_label_and_the_proposal_key()
    {
        // Arrange
        var person = Person.CreateFromProposedName("ten_1", "usr_1", "Jane");
        var key = person.ProposalKey;

        // Act
        person.Describe("Jane Park", "Sam's teacher", person.Version);

        // Assert
        person.Label.ShouldBe("Jane");
        person.ProposalKey.ShouldBe(key);
        person.FullName.ShouldBe("Jane Park");
        person.Notes.ShouldBe("Sam's teacher");
    }

    [Test]
    [Property("Rule", "SES-55")]
    public void a_rename_keeps_the_details()
    {
        // Arrange
        var person = Person.Create("ten_1", "usr_1", "Jane", "Jane Park", "Sam's teacher");

        // Act
        person.Rename("Janie");

        // Assert
        person.FullName.ShouldBe("Jane Park");
        person.Notes.ShouldBe("Sam's teacher");
    }

    [Test]
    [Property("Rule", "SES-55")]
    public void a_detail_that_is_too_long_is_refused_and_the_details_stay()
    {
        // Arrange
        var person = Person.Create("ten_1", "usr_1", "Jane", "Jane Park");

        // Act
        var describe = () => person.Describe(new string('a', Person.FullNameMaxLength + 1), null, person.Version);

        // Assert
        describe.ShouldThrow<ValidationException>().Property.ShouldBe("fullName");
        person.FullName.ShouldBe("Jane Park");
    }
}
