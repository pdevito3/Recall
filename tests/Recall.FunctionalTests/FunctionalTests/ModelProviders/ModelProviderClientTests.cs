namespace Recall.FunctionalTests.FunctionalTests.ModelProviders;

using Microsoft.Extensions.DependencyInjection;
using Recall.FakeModelProvider;
using Recall.Server.Domain.ModelProviders;
using Recall.Server.Domain.ModelProviders.Clients;
using Shouldly;

/// <summary>Each adapter against a server that speaks its wire format, over real HTTP.</summary>
public class ModelProviderClientTests : TestBase
{
    private const string Instructions = "Clean this up.";

    private static IModelProviderClient Client => TestingWebApplicationFactory.Instance.Services.GetRequiredService<IModelProviderClient>();

    private static ModelEndpoint EndpointOf(FakeModelProvider fake, ModelProviderKind kind, string? key)
        => new(kind, CleanupScenario.BaseUrlOf(fake, kind), key);

    private static Task<FakeModelProvider> FakeWithAsync(string key, FakeBehavior behavior) => FakeModelProvider.StartAsync(key).ContinueWith(task =>
    {
        task.Result.Behavior.Mode = behavior.Mode;
        task.Result.Behavior.Status = behavior.Status;
        task.Result.Behavior.DelayMs = behavior.DelayMs;
        task.Result.Behavior.Reply = behavior.Reply;
        return task.Result;
    }, TaskScheduler.Default);

    private static ProviderFailure FailureOf<T>(ProviderReply<T> reply) => reply.ShouldBeOfType<ProviderReply<T>.Failed>().Failure;

    [Test]
    [Arguments(ModelProviderKind.Anthropic)]
    [Arguments(ModelProviderKind.OpenAiCompatible)]
    public async Task the_model_list_comes_from_the_provider(ModelProviderKind kind)
    {
        // Arrange
        var key = CleanupScenario.NewKey();
        await using var fake = await FakeModelProvider.StartAsync(key);

        // Act
        var reply = await Client.ListModelsAsync(EndpointOf(fake, kind, key), CancellationToken.None);

        // Assert
        reply.ShouldBeOfType<ProviderReply<IReadOnlyList<ProviderModel>>.Answered>().Value
            .Select(model => model.Id).ShouldBe([FakeModelProvider.ModelId, FakeModelProvider.SecondModelId]);
    }

    [Test]
    [Arguments(ModelProviderKind.Anthropic)]
    [Arguments(ModelProviderKind.OpenAiCompatible)]
    public async Task a_wrong_key_gives_the_message_of_the_provider(ModelProviderKind kind)
    {
        // Arrange
        await using var fake = await FakeModelProvider.StartAsync(CleanupScenario.NewKey());

        // Act
        var reply = await Client.ListModelsAsync(EndpointOf(fake, kind, CleanupScenario.NewKey()), CancellationToken.None);

        // Assert
        var failure = FailureOf(reply);
        failure.Kind.ShouldBe(ProviderFailureKind.Refused);
        failure.StatusCode.ShouldBe(401);
        failure.DisplayMessage.ShouldContain(kind == ModelProviderKind.Anthropic ? "invalid x-api-key" : "Incorrect API key");
    }

    [Test]
    [Arguments(ModelProviderKind.Anthropic)]
    [Arguments(ModelProviderKind.OpenAiCompatible)]
    public async Task a_completion_sends_the_words_and_gives_the_answer(ModelProviderKind kind)
    {
        // Arrange
        var key = CleanupScenario.NewKey();
        await using var fake = await FakeModelProvider.StartAsync(key);

        // Act
        var reply = await Client.CompleteAsync(EndpointOf(fake, kind, key), FakeModelProvider.ModelId, Instructions, "<raw>\nsome words\n</raw>", CancellationToken.None);

        // Assert
        reply.ShouldBeOfType<ProviderReply<string>.Answered>().Value.ShouldContain("some words (cleaned)");
        fake.CompletionCalls.ShouldBe(1);
    }

    [Test]
    [Property("Rule", "SES-37")]
    public async Task an_anthropic_stream_that_stalls_after_its_first_events_ends_at_the_call_limit()
    {
        // Arrange
        var key = CleanupScenario.NewKey();
        await using var fake = await FakeWithAsync(key, new FakeBehavior { Mode = FakeMode.StallMidStream });
        var factory = TestingWebApplicationFactory.Instance.Services.GetRequiredService<IHttpClientFactory>();
        var client = new AnthropicModelClient(factory) { CallLimit = TimeSpan.FromSeconds(2) };

        // Act
        var reply = await client.CompleteAsync(EndpointOf(fake, ModelProviderKind.Anthropic, key), FakeModelProvider.ModelId, Instructions, "some words", CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(30));

        // Assert
        FailureOf(reply).Kind.ShouldBe(ProviderFailureKind.Unavailable);
    }

    [Test]
    [Property("Rule", "SES-37")]
    public async Task a_stop_of_the_host_during_a_stalled_stream_is_a_cancel_and_no_failure_of_the_provider()
    {
        // Arrange
        var key = CleanupScenario.NewKey();
        await using var fake = await FakeWithAsync(key, new FakeBehavior { Mode = FakeMode.StallMidStream });
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(1));

        // Act
        var call = () => Client.CompleteAsync(EndpointOf(fake, ModelProviderKind.Anthropic, key), FakeModelProvider.ModelId, Instructions, "some words", stop.Token)
            .WaitAsync(TimeSpan.FromSeconds(30));

        // Assert
        await call.ShouldThrowAsync<OperationCanceledException>();
    }

    [Test]
    public async Task an_anthropic_request_sends_the_cap_of_the_model_and_no_temperature()
    {
        // Arrange
        var key = CleanupScenario.NewKey();
        await using var fake = await FakeModelProvider.StartAsync(key);
        var longText = new string('x', 900_000);

        // Act
        var reply = await Client.CompleteAsync(EndpointOf(fake, ModelProviderKind.Anthropic, key), FakeModelProvider.ModelId, Instructions, longText, CancellationToken.None);

        // Assert
        reply.ShouldBeOfType<ProviderReply<string>.Answered>();
        fake.RequestBodies.ShouldHaveSingleItem().ShouldContain($"\"max_tokens\":{FakeModelProvider.ModelMaxTokens}");
    }

    [Test]
    [Arguments(ModelProviderKind.Anthropic, FakeMode.Truncate)]
    [Arguments(ModelProviderKind.OpenAiCompatible, FakeMode.Truncate)]
    [Arguments(ModelProviderKind.Anthropic, FakeMode.Refusal)]
    [Arguments(ModelProviderKind.OpenAiCompatible, FakeMode.Refusal)]
    public async Task an_answer_that_did_not_stop_by_itself_is_no_answer(ModelProviderKind kind, FakeMode mode)
    {
        // Arrange
        var key = CleanupScenario.NewKey();
        await using var fake = await FakeWithAsync(key, new FakeBehavior { Mode = mode });

        // Act
        var reply = await Client.CompleteAsync(EndpointOf(fake, kind, key), FakeModelProvider.ModelId, Instructions, "some words", CancellationToken.None);

        // Assert
        FailureOf(reply).Kind.ShouldBe(ProviderFailureKind.BadOutput);
    }

    [Test]
    [Arguments(ModelProviderKind.Anthropic, 429, ProviderFailureKind.Unavailable)]
    [Arguments(ModelProviderKind.OpenAiCompatible, 429, ProviderFailureKind.Unavailable)]
    [Arguments(ModelProviderKind.Anthropic, 529, ProviderFailureKind.Unavailable)]
    [Arguments(ModelProviderKind.OpenAiCompatible, 503, ProviderFailureKind.Unavailable)]
    [Arguments(ModelProviderKind.Anthropic, 402, ProviderFailureKind.Refused)]
    [Arguments(ModelProviderKind.OpenAiCompatible, 403, ProviderFailureKind.Refused)]
    public async Task a_status_decides_the_failure_and_the_adapter_does_not_retry(ModelProviderKind kind, int status, ProviderFailureKind expected)
    {
        // Arrange
        var key = CleanupScenario.NewKey();
        await using var fake = await FakeWithAsync(key, new FakeBehavior { Mode = FakeMode.Status, Status = status });

        // Act
        var reply = await Client.CompleteAsync(EndpointOf(fake, kind, key), FakeModelProvider.ModelId, Instructions, "some words", CancellationToken.None);

        // Assert
        FailureOf(reply).Kind.ShouldBe(expected);
        fake.CompletionCalls.ShouldBe(1);
    }

    [Test]
    [Arguments(ModelProviderKind.Anthropic)]
    [Arguments(ModelProviderKind.OpenAiCompatible)]
    public async Task a_redirect_does_not_take_the_key_to_another_address(ModelProviderKind kind)
    {
        // Arrange
        var key = CleanupScenario.NewKey();
        await using var fake = await FakeWithAsync(key, new FakeBehavior { Mode = FakeMode.Redirect });

        // Act
        var reply = await Client.CompleteAsync(EndpointOf(fake, kind, key), FakeModelProvider.ModelId, Instructions, "some words", CancellationToken.None);

        // Assert
        reply.ShouldBeOfType<ProviderReply<string>.Failed>();
        fake.StolenCalls.ShouldBe(0);
    }

    [Test]
    public async Task an_error_in_an_anthropic_stream_gives_a_failure_with_no_words_of_the_request()
    {
        // Arrange
        var key = CleanupScenario.NewKey();
        var marker = $"PRIVATEWORDS{Guid.NewGuid():N}";
        await using var fake = await FakeWithAsync(key, new FakeBehavior { Mode = FakeMode.MidStreamError });

        // Act
        var reply = await Client.CompleteAsync(EndpointOf(fake, ModelProviderKind.Anthropic, key), FakeModelProvider.ModelId, Instructions, marker, CancellationToken.None);

        // Assert
        var failure = FailureOf(reply);
        failure.Kind.ShouldBe(ProviderFailureKind.Unavailable);
        failure.DisplayMessage.ShouldNotContain(marker);
        failure.DisplayMessage.ShouldNotContain(key);
    }

    [Test]
    [Arguments(ModelProviderKind.Anthropic)]
    [Arguments(ModelProviderKind.OpenAiCompatible)]
    public async Task the_failure_of_a_provider_prints_no_words_and_its_shown_message_has_no_key(ModelProviderKind kind)
    {
        // Arrange
        var key = CleanupScenario.NewKey();
        var marker = $"PRIVATEWORDS{Guid.NewGuid():N}";
        await using var fake = await FakeWithAsync(key, new FakeBehavior { Mode = FakeMode.EchoError });

        // Act
        var reply = await Client.CompleteAsync(EndpointOf(fake, kind, key), FakeModelProvider.ModelId, Instructions, marker, CancellationToken.None);

        // Assert
        reply.ToString().ShouldNotBeNull().ShouldNotContain(marker);
        reply.ToString().ShouldNotBeNull().ShouldNotContain(key);
        FailureOf(reply).DisplayMessage.ShouldNotContain(key);
    }

    [Test]
    [Arguments(ModelProviderKind.Anthropic)]
    [Arguments(ModelProviderKind.OpenAiCompatible)]
    public async Task a_stop_of_the_host_is_no_failure_of_the_provider(ModelProviderKind kind)
    {
        // Arrange
        var key = CleanupScenario.NewKey();
        await using var fake = await FakeWithAsync(key, new FakeBehavior { Mode = FakeMode.Hold, DelayMs = 60_000 });
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        // Act
        var call = () => Client.CompleteAsync(EndpointOf(fake, kind, key), FakeModelProvider.ModelId, Instructions, "some words", stop.Token);

        // Assert
        await call.ShouldThrowAsync<OperationCanceledException>();
    }
}
