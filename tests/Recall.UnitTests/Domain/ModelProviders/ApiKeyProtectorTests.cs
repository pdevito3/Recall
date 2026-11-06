namespace Recall.UnitTests.Domain.ModelProviders;

using Microsoft.AspNetCore.DataProtection;
using Recall.Server.Domain.ModelProviders;
using Recall.Server.Domain.ModelProviders.Clients;
using Shouldly;

public class ApiKeyProtectorTests
{
    private static ApiKeyProtector ProtectorWithNewRing()
        => new(DataProtectionProvider.Create(Directory.CreateTempSubdirectory("recall-ring-test")));

    private static string NewKey() => $"fake-key-{Guid.NewGuid():N}";

    [Test]
    public void a_protected_key_does_not_hold_the_key_and_reads_back()
    {
        // Arrange
        var protector = ProtectorWithNewRing();
        var key = NewKey();

        // Act
        var protectedKey = protector.Protect(key);

        // Assert
        protectedKey.ShouldNotBeNull().ShouldNotContain(key);
        protector.Read(protectedKey).ShouldBeOfType<ProtectedKeyRead.Readable>().ApiKey.ShouldBe(key);
    }

    [Test]
    public void a_key_of_another_ring_is_unreadable_and_no_exception()
    {
        // Arrange
        var protectedKey = ProtectorWithNewRing().Protect(NewKey());

        // Act
        var read = ProtectorWithNewRing().Read(protectedKey);

        // Assert
        read.ShouldBeOfType<ProtectedKeyRead.Unreadable>();
        ApiKeyProtector.StateOf(read).ShouldBe(ModelProviderKeyState.Unreadable);
    }

    [Test]
    public void no_key_is_no_failure()
    {
        // Arrange
        var protector = ProtectorWithNewRing();

        // Act
        var read = protector.Read(null);

        // Assert
        protector.Protect(null).ShouldBeNull();
        read.ShouldBeOfType<ProtectedKeyRead.NoKey>();
        ApiKeyProtector.StateOf(read).ShouldBe(ModelProviderKeyState.None);
    }

    [Test]
    public void the_text_of_a_read_does_not_print_the_key()
    {
        // Arrange
        var key = NewKey();

        // Act
        var text = new ProtectedKeyRead.Readable(key).ToString();

        // Assert
        text.ShouldNotContain(key);
    }

    [Test]
    [Arguments(true, false, KeyRingState.Same)]
    [Arguments(true, true, KeyRingState.Changed)]
    [Arguments(false, false, KeyRingState.NotWritable)]
    [Arguments(false, true, KeyRingState.NotWritable)]
    public void the_state_of_the_ring_comes_from_the_folder_and_the_keys(bool writable, bool unreadable, KeyRingState expected)
    {
        // Arrange & Act & Assert
        KeyRingStatus.StateFor(writable, unreadable).ShouldBe(expected);
    }

    [Test]
    public void a_shown_message_of_a_provider_has_no_key_in_it()
    {
        // Arrange
        var key = NewKey();

        // Act
        var shown = ProviderFailureText.Shown($"Bad key {key} and again {key}.", key, "fallback");

        // Assert
        shown.ShouldBe("Bad key [removed] and again [removed].");
    }

    [Test]
    public void a_body_that_is_no_error_of_a_provider_is_not_shown()
    {
        // Act
        var page = ProviderFailureText.ForStatus(502, "<html>the page of a proxy</html>", null);
        var text = ProviderFailureText.ForStatus(400, "{\"error\":\"a string\"}", null);

        // Assert
        page.DisplayMessage.ShouldBe("The model provider answered with the status 502.");
        text.DisplayMessage.ShouldBe("The model provider answered with the status 400.");
    }

    [Test]
    [Arguments(401, ProviderFailureKind.Refused)]
    [Arguments(402, ProviderFailureKind.Refused)]
    [Arguments(404, ProviderFailureKind.Refused)]
    [Arguments(408, ProviderFailureKind.Unavailable)]
    [Arguments(429, ProviderFailureKind.Unavailable)]
    [Arguments(500, ProviderFailureKind.Unavailable)]
    [Arguments(529, ProviderFailureKind.Unavailable)]
    public void a_status_gives_the_kind_of_failure(int status, ProviderFailureKind expected)
    {
        // Arrange & Act & Assert
        ProviderFailureText.ForStatus(status, null, null).Kind.ShouldBe(expected);
    }
}
