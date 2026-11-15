namespace Recall.UnitTests.Domain.Users;

using Recall.Server.Domain.Users;
using Recall.Server.Exceptions;
using Shouldly;

public class TokenGrantTests
{
    public static IEnumerable<Func<TokenGrant>> Grants()
    {
        yield return () => new TokenGrant([], JournalAccess.None);
        yield return () => new TokenGrant([Permission.ReadUsers()], JournalAccess.Read);
        yield return () => new TokenGrant([Permission.ManageUsers(), Permission.ReadAuditLog()], JournalAccess.ReadWrite);
        yield return () => new TokenGrant(Permission.GetAll(), JournalAccess.ReadWrite);
    }

    [Test]
    [MethodDataSource(nameof(Grants))]
    public void a_grant_reads_back_from_its_text(TokenGrant grant)
    {
        // Act
        var decoded = TokenGrant.Decode(grant.Encode());

        // Assert
        decoded.ShouldBe(grant);
        decoded.Journal.ShouldBe(grant.Journal);
        decoded.Permissions.ShouldBe(grant.Permissions, ignoreOrder: true);
    }

    [Test]
    public void the_text_of_a_grant_does_not_depend_on_the_order_of_its_permissions()
    {
        // Arrange
        var one = new TokenGrant([Permission.ReadUsers(), Permission.ManageUsers()], JournalAccess.Read);
        var other = new TokenGrant([Permission.ManageUsers(), Permission.ReadUsers()], JournalAccess.Read);

        // Act & Assert
        one.Encode().ShouldBe("read:manage_users,read_users");
        other.Encode().ShouldBe(one.Encode());
    }

    [Test]
    public void a_permission_name_that_the_build_does_not_know_is_dropped()
    {
        // Act
        var grant = TokenGrant.Decode("read_write:manage_users,manage_spaceships,read_users");

        // Assert
        grant.Permissions.ShouldBe([Permission.ManageUsers(), Permission.ReadUsers()], ignoreOrder: true);
        grant.Journal.ShouldBe(JournalAccess.ReadWrite);
    }

    [Test]
    [Arguments("write:manage_users")]
    [Arguments("READ_WRITE:manage_users")]
    [Arguments("manage_users")]
    [Arguments("")]
    public void a_journal_access_that_the_build_does_not_know_grants_no_journal(string text)
    {
        // Act
        var grant = TokenGrant.Decode(text);

        // Assert
        grant.Journal.ShouldBe(JournalAccess.None);
    }

    [Test]
    [Property("Rule", "PAT-5")]
    [Arguments(false, false, "none")]
    [Arguments(true, false, "read")]
    [Arguments(true, true, "read_write")]
    public void the_journal_access_of_a_grant_is_none_read_or_read_write(bool read, bool write, string access)
    {
        // Act
        var journal = JournalAccess.Of(read, write);

        // Assert
        journal.Name.ShouldBe(access);
        journal.CanRead.ShouldBe(read);
        journal.CanWrite.ShouldBe(write);
    }

    [Test]
    [Property("Rule", "PAT-5")]
    public void write_without_read_is_refused()
    {
        // Act
        var act = () => JournalAccess.Of(read: false, write: true);

        // Assert
        var refusal = act.ShouldThrow<ValidationException>();
        refusal.Property.ShouldBe("journal");
        refusal.Message.ShouldBe("A token that changes your journal must also read it.");
    }
}
