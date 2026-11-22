namespace Recall.UnitTests.Auth;

using Recall.Server.Auth;
using Shouldly;

public sealed class TemporaryPasswordTests
{
    [Test]
    [Property("Rule", "PWD-14")]
    public void the_word_list_is_the_eff_long_list()
    {
        // Act
        var words = TemporaryPassword.Words;

        // Assert
        words.Count.ShouldBe(7776);
        words.Distinct(StringComparer.Ordinal).Count().ShouldBe(7776);
        words.ShouldAllBe(w => w.Length >= 3 && w.All(c => char.IsAsciiLetterLower(c) || c == '-'));
    }

    [Test]
    [Property("Rule", "PWD-14")]
    public void a_temporary_password_is_four_words_of_the_list_joined_by_a_dash()
    {
        // Act
        var password = TemporaryPassword.New();

        // Assert
        // Four words of the list, such as drop-down, hold a dash, so a password can have more than three.
        password.ShouldMatch("^[a-z]+(-[a-z]+){3,7}$");
        password.Length.ShouldBeGreaterThanOrEqualTo(15);
    }

    [Test]
    [Property("Rule", "PWD-14")]
    public void each_word_is_picked_from_the_whole_list()
    {
        // Arrange
        var picks = new Queue<int>([0, 7775, 1, 7774]);
        var sizes = new List<int>();

        // Act
        var password = TemporaryPassword.From(count =>
        {
            sizes.Add(count);
            return picks.Dequeue();
        });

        // Assert
        var words = TemporaryPassword.Words;
        password.ShouldBe($"{words[0]}-{words[7775]}-{words[1]}-{words[7774]}");
        sizes.ShouldBe([7776, 7776, 7776, 7776]);
    }

    [Test]
    [Property("Rule", "PWD-14")]
    public void two_temporary_passwords_differ()
    {
        // Act
        var passwords = Enumerable.Range(0, 20).Select(_ => TemporaryPassword.New()).ToHashSet(StringComparer.Ordinal);

        // Assert
        passwords.Count.ShouldBe(20);
    }
}
