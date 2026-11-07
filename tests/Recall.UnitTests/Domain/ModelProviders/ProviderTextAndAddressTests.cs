namespace Recall.UnitTests.Domain.ModelProviders;

using System.Net;
using System.Text;
using Recall.Server.Domain.ModelProviders;
using Recall.Server.Domain.ModelProviders.Clients;
using Shouldly;

public class ProviderTextAndAddressTests
{
    private const string Key = "sk-ant-api03-Secret+Value/With=Marks_0123456789";

    private static string Base64 => Convert.ToBase64String(Encoding.UTF8.GetBytes(Key));

    public static IEnumerable<Func<string>> FormsOfTheKey()
    {
        yield return () => Key;
        yield return () => Key.ToUpperInvariant();
        yield return () => Key.ToLowerInvariant();
        yield return () => Key[..16];
        yield return () => Key[^16..];
        yield return () => Uri.EscapeDataString(Key);
        yield return () => Uri.EscapeDataString(Key).ToLowerInvariant();
        yield return () => Base64;
        yield return () => Base64[..20];
        yield return () => Base64.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    [Test]
    [MethodDataSource(nameof(FormsOfTheKey))]
    public void a_form_of_the_key_in_a_message_is_removed(string form)
    {
        // Act
        var shown = ProviderFailureText.Shown($"Bad key <{form}> in this request.", Key, "fallback");

        // Assert
        shown.ShouldNotContain(form, Case.Insensitive);
        shown.ShouldContain("[removed]");
        shown.ShouldContain("Bad key");
        shown.ShouldContain("in this request.");
    }

    [Test]
    public void a_message_with_no_form_of_the_key_stays_whole()
    {
        ProviderFailureText.Shown("Your credit balance is too low.", Key, "fallback").ShouldBe("Your credit balance is too low.");
    }

    [Test]
    public void a_key_at_the_cut_of_the_length_limit_is_removed_before_the_cut()
    {
        // Act
        var shown = ProviderFailureText.Shown(new string('.', 290) + Key, Key, "fallback");

        // Assert
        shown.ShouldNotContain(Key[..8]);
    }

    [Test]
    [Arguments("before\u0000after", "beforeafter")]
    [Arguments("a‮b⁦c", "abc")]
    [Arguments("line\nnext", "line\nnext")]
    public void a_hidden_character_does_not_reach_a_message(string message, string shown)
    {
        ProviderFailureText.Shown(message, null, "fallback").ShouldBe(shown);
    }

    [Test]
    [Arguments(ModelProviderKind.Anthropic, """{"type":"error","error":{"type":"not_found_error","message":"shown"},"request_id":"r"}""", "shown")]
    [Arguments(ModelProviderKind.Anthropic, """{"error":{"message":"another service"}}""", null)]
    [Arguments(ModelProviderKind.Anthropic, """{"type":"error","error":{"message":"another service"}}""", null)]
    [Arguments(ModelProviderKind.OpenAiCompatible, """{"error":{"message":"shown","type":"invalid_request_error","code":null}}""", "shown")]
    [Arguments(ModelProviderKind.OpenAiCompatible, """{"error":{"message":"shown","code":404}}""", "shown")]
    [Arguments(ModelProviderKind.OpenAiCompatible, """{"error":{"message":"another service"}}""", null)]
    [Arguments(ModelProviderKind.OpenAiCompatible, """{"error":"another service"}""", null)]
    [Arguments(ModelProviderKind.OpenAiCompatible, "<html>another service</html>", null)]
    [Arguments(ModelProviderKind.OpenAiCompatible, "secret words of a service on the network", null)]
    public void only_the_error_format_of_the_provider_shows_a_message(ModelProviderKind kind, string body, string? expected)
    {
        ProviderFailureText.MessageIn(kind, body).ShouldBe(expected);
    }

    [Test]
    [Arguments("169.254.169.254", false, true)]
    [Arguments("169.254.0.1", false, true)]
    [Arguments("::ffff:169.254.169.254", false, true)]
    [Arguments("::169.254.169.254", false, true)]
    [Arguments("64:ff9b::a9fe:a9fe", false, true)]
    [Arguments("2002:a9fe:a9fe::1", false, true)]
    [Arguments("fe80::1", false, true)]
    [Arguments("fd00:ec2::254", false, true)]
    [Arguments("100.100.100.200", false, true)]
    [Arguments("168.63.129.16", false, true)]
    [Arguments("192.0.0.192", false, true)]
    [Arguments("0.0.0.0", false, true)]
    [Arguments("0.1.2.3", false, true)]
    [Arguments("::", false, true)]
    [Arguments("224.0.0.1", false, true)]
    [Arguments("255.255.255.255", false, true)]
    [Arguments("ff02::1", false, true)]
    [Arguments("127.0.0.1", true, true)]
    [Arguments("127.8.8.8", true, true)]
    [Arguments("::1", true, true)]
    [Arguments("::ffff:127.0.0.1", true, true)]
    [Arguments("127.0.0.1", false, false)]
    [Arguments("::1", false, false)]
    [Arguments("10.0.0.5", true, false)]
    [Arguments("172.16.0.5", true, false)]
    [Arguments("192.168.1.20", true, false)]
    [Arguments("100.64.0.5", true, false)]
    [Arguments("fd12:3456::1", true, false)]
    [Arguments("8.8.8.8", true, false)]
    [Arguments("2606:4700::1111", true, false)]
    public void the_address_rule_names_each_range(string address, bool loopbackIsTheApp, bool refused)
    {
        ProviderAddressRule.Refuses(IPAddress.Parse(address), loopbackIsTheApp).ShouldBe(refused);
    }

    [Test]
    [Arguments("http://169.254.169.254/v1")]
    [Arguments("http://[fe80::1]:8080/v1")]
    [Arguments("http://[::ffff:169.254.169.254]/v1")]
    public void a_refused_address_cannot_be_saved(string baseUrl)
    {
        var save = () => ModelProviderAddress.Normalized(baseUrl);
        save.ShouldThrow<Recall.Server.Exceptions.ValidationException>();
    }

    [Test]
    [Arguments("http://192.168.1.20:11434/v1")]
    [Arguments("https://api.example.com/v1")]
    public void a_private_or_a_public_address_can_be_saved(string baseUrl)
    {
        ModelProviderAddress.Normalized(baseUrl).ShouldBe(baseUrl);
    }
}
