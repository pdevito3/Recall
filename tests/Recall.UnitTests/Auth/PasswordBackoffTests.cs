namespace Recall.UnitTests.Auth;

using System.Net;
using Microsoft.Extensions.Time.Testing;
using Recall.Server.Auth;
using Shouldly;

public sealed class PasswordBackoffTests
{
    private static readonly IPAddress Address = IPAddress.Parse("192.0.2.1");

    [Test]
    [Property("Rule", "PWD-12")]
    public void above_the_cap_the_pairs_with_the_oldest_wrong_try_go_and_the_newest_stay()
    {
        // Arrange
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 21, 10, 0, 0, TimeSpan.Zero));
        var backoff = new PasswordBackoff(clock);
        for (var i = 0; i < PasswordBackoff.FreeTries; i++)
            backoff.NoteWrong("oldest", Address);

        // Act
        for (var i = 0; i < PasswordBackoff.MaxPairs; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(1));
            backoff.NoteWrong($"made-up-{i}", Address);
        }

        for (var i = 0; i < PasswordBackoff.FreeTries; i++)
            backoff.NoteWrong("newest", Address);

        // Assert
        backoff.Count.ShouldBeLessThanOrEqualTo(PasswordBackoff.MaxPairs);
        backoff.WaitLeft("oldest", Address).ShouldBeNull();
        backoff.WaitLeft("NEWEST", Address).ShouldNotBeNull();
    }

    [Test]
    [Property("Rule", "PWD-12")]
    public void a_long_typed_name_keys_its_pair_as_a_short_one_does()
    {
        // Arrange
        var backoff = new PasswordBackoff(new FakeTimeProvider());
        var longName = new string('a', 100_000);

        // Act
        for (var i = 0; i < PasswordBackoff.FreeTries; i++)
            backoff.NoteWrong(longName, Address);

        // Assert
        backoff.Count.ShouldBe(1);
        backoff.WaitLeft(longName.ToUpperInvariant(), Address).ShouldNotBeNull();
        backoff.WaitLeft(longName, IPAddress.Parse("192.0.2.2")).ShouldBeNull();
    }
}
