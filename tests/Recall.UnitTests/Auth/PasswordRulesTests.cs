namespace Recall.UnitTests.Auth;

using Recall.Server.Auth;
using Recall.Server.Auth.Entities;
using Shouldly;

public sealed class PasswordRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 10, 0, 0, TimeSpan.Zero);

    private static LoginName Name(string typed)
    {
        LoginName.TryOf(typed, out var name).ShouldBeTrue(typed);
        return name;
    }

    [Test]
    [Property("Rule", "PWD-18")]
    [Arguments("Ada", "ada")]
    [Arguments("ADA.Lovelace-1_x@home", "ada.lovelace-1_x@home")]
    [Arguments("ＡＤＡ", "ada")]
    [Arguments("Ελένη", "ελένη")]
    [Arguments("ﬁsh", "fish")]
    public void a_login_name_is_stored_after_nfkc_and_a_case_fold(string typed, string stored)
    {
        // Act
        var name = Name(typed);

        // Assert
        name.Value.ShouldBe(stored);
        LoginName.KeyOf(typed).ShouldBe(stored);
    }

    [Test]
    [Property("Rule", "PWD-18")]
    [Arguments("")]
    [Arguments(" ")]
    [Arguments("ada lovelace")]
    [Arguments("ada!")]
    [Arguments("ada\0")]
    [Arguments("\uD800")]
    [Arguments("ada/../x")]
    public void a_login_name_with_another_character_is_refused(string typed)
    {
        // Act & Assert
        LoginName.TryOf(typed, out _).ShouldBeFalse();
    }

    [Test]
    [Property("Rule", "PWD-18")]
    [Arguments("a")]
    // DESERET SMALL LETTER LONG I stays itself under NFKC and the case fold, so each code point is two UTF-16 units.
    [Arguments("\U00010428")]
    public void a_login_name_takes_64_code_points_and_refuses_65(string character)
    {
        // Arrange
        var fits = string.Concat(Enumerable.Repeat(character, LoginName.MaxLength));
        var tooLong = fits + character;

        // Act
        var takes = LoginName.TryOf(fits, out var name);
        var refuses = !LoginName.TryOf(tooLong, out _);

        // Assert
        takes.ShouldBeTrue();
        name!.Value.ShouldBe(fits);
        name.Value.Length.ShouldBe(LoginName.MaxLength * character.Length);
        name.Value.Length.ShouldBeLessThanOrEqualTo(LoginName.MaxStoredLength);
        refuses.ShouldBeTrue();
    }

    [Test]
    [Property("Rule", "PWD-20")]
    public void a_login_name_and_a_credential_show_no_value_as_text()
    {
        // Arrange
        var credential = PasswordCredential.Choose("usr_1", Name("ada"), "AQAAAAIAAhash", Now);

        // Act & Assert
        Name("ada").ToString().ShouldNotContain("ada");
        credential.ToString().ShouldNotContain("hash");
        new PasswordChoice.Accepted("a secret password here").ToString().ShouldNotContain("secret");
    }

    [Test]
    [Property("Rule", "PWD-8")]
    [Arguments("fourteen chars")]
    [Arguments("")]
    public void a_password_under_15_code_points_is_refused(string password)
    {
        // Act
        var choice = PasswordPolicy.Check(password, Name("ada"), []);

        // Assert
        choice.ShouldBe(new PasswordChoice.Refused(PasswordPolicy.TooShortMessage));
    }

    [Test]
    [Property("Rule", "PWD-8")]
    public void the_length_counts_code_points_after_nfkc()
    {
        // Arrange - 15 emoji are 30 code units, and 14 ligatures are 28 letters after NFKC
        var emoji = string.Concat(Enumerable.Repeat("\U0001F600", 15));
        var ligatures = string.Concat(Enumerable.Repeat("ﬁ", 8));

        // Act
        var emojiChoice = PasswordPolicy.Check(emoji, Name("ada"), []);
        var ligatureChoice = PasswordPolicy.Check(ligatures, Name("ada"), []);

        // Assert
        emojiChoice.ShouldBe(new PasswordChoice.Accepted(emoji));
        ligatureChoice.ShouldBe(new PasswordChoice.Accepted(string.Concat(Enumerable.Repeat("fi", 8))));
    }

    [Test]
    [Property("Rule", "PWD-8")]
    public void a_password_takes_256_code_points_each_character_and_spaces_and_refuses_257()
    {
        // Arrange
        var fits = string.Concat(Enumerable.Repeat("ü ", 128));

        // Act
        var takes = PasswordPolicy.Check(fits, Name("ada"), []);
        var refuses = PasswordPolicy.Check(fits + "x", Name("ada"), []);

        // Assert
        takes.ShouldBe(new PasswordChoice.Accepted(fits));
        refuses.ShouldBe(new PasswordChoice.Refused(PasswordPolicy.TooLongMessage));
    }

    [Test]
    [Property("Rule", "PWD-9")]
    [Arguments("passwordpassword")]
    [Arguments("PasswordPassword")]
    public void a_password_on_the_shipped_list_is_refused_with_case_ignored(string password)
    {
        // Act
        var choice = PasswordPolicy.Check(password, Name("ada"), []);

        // Assert
        choice.ShouldBe(new PasswordChoice.Refused(PasswordPolicy.CommonMessage));
    }

    [Test]
    [Property("Rule", "PWD-9")]
    [Arguments("ada.lovelace.1815")]
    [Arguments("Augusta Ada King")]
    public void a_password_that_is_the_login_name_or_a_name_of_the_person_is_refused(string password)
    {
        // Act
        var choice = PasswordPolicy.Check(password, Name("Ada.Lovelace.1815"), ["Augusta", "Augusta Ada King"]);

        // Assert
        choice.ShouldBe(new PasswordChoice.Refused(PasswordPolicy.PersonalMessage));
    }

    [Test]
    [Property("Rule", "PWD-9")]
    public void a_long_password_that_holds_the_name_but_is_not_the_name_is_taken()
    {
        // Act
        var choice = PasswordPolicy.Check("ada lovelace rides a tram", Name("ada"), ["Ada Lovelace"]);

        // Assert
        choice.ShouldBeOfType<PasswordChoice.Accepted>();
    }

    [Test]
    [Property("Rule", "PWD-13")]
    public void the_100th_wrong_password_in_a_row_stops_the_credential_and_a_right_one_resets_the_count()
    {
        // Arrange
        var reset = PasswordCredential.Choose("usr_1", Name("ada"), "hash", Now);
        var stopped = PasswordCredential.Choose("usr_2", Name("bea"), "hash", Now);
        for (var i = 0; i < PasswordCredential.StopAt - 1; i++)
        {
            reset.NoteFailure();
            stopped.NoteFailure();
        }

        // Act
        reset.NoteSuccess();
        stopped.NoteFailure();
        stopped.NoteFailure();

        // Assert
        reset.FailedCount.ShouldBe(0);
        reset.State.ShouldBe(PasswordState.Chosen);
        stopped.State.ShouldBe(PasswordState.Stopped);
        stopped.FailedCount.ShouldBe(PasswordCredential.StopAt);
        Should.Throw<InvalidOperationException>(stopped.NoteSuccess);
    }

    [Test]
    [Property("Rule", "PWD-15")]
    public void a_temporary_password_lasts_7_days()
    {
        // Arrange
        var credential = PasswordCredential.IssueTemporary("usr_1", Name("ada"), "hash", Now);

        // Act
        var beforeTheEnd = credential.IsExpired(Now + TimeSpan.FromDays(7) - TimeSpan.FromSeconds(1));
        var atTheEnd = credential.IsExpired(Now + TimeSpan.FromDays(7));

        // Assert
        credential.State.ShouldBe(PasswordState.Temporary);
        credential.TemporaryUntil.ShouldBe(Now + TimeSpan.FromDays(7));
        beforeTheEnd.ShouldBeFalse();
        atTheEnd.ShouldBeTrue();
    }

    [Test]
    [Property("Rule", "PWD-16")]
    public void a_choice_makes_a_temporary_credential_chosen_and_refuses_an_expired_one()
    {
        // Arrange
        var live = PasswordCredential.IssueTemporary("usr_1", Name("ada"), "temporary", Now);
        var expired = PasswordCredential.IssueTemporary("usr_2", Name("bea"), "temporary", Now);
        var later = Now + TimeSpan.FromHours(1);

        // Act
        live.Choose("chosen", later);
        var chooseExpired = () => expired.Choose("chosen", Now + TimeSpan.FromDays(8));
        var chooseAgain = () => live.Choose("again", later);

        // Assert
        live.State.ShouldBe(PasswordState.Chosen);
        live.PasswordHash.ShouldBe("chosen");
        live.ChangedAt.ShouldBe(later);
        live.TemporaryUntil.ShouldBeNull();
        chooseExpired.ShouldThrow<InvalidOperationException>();
        chooseAgain.ShouldThrow<InvalidOperationException>();
    }

    [Test]
    [Property("Rule", "PWD-13")]
    public void a_new_temporary_password_ends_a_stop_and_keeps_the_login_name()
    {
        // Arrange
        var credential = PasswordCredential.Choose("usr_1", Name("ada"), "hash", Now);
        for (var i = 0; i < PasswordCredential.StopAt; i++)
            credential.NoteFailure();

        // Act
        credential.IssueTemporary("temporary", Now);

        // Assert
        credential.State.ShouldBe(PasswordState.Temporary);
        credential.FailedCount.ShouldBe(0);
        credential.LoginName.ShouldBe(Name("ada"));
    }
}
