namespace Recall.FunctionalTests.FunctionalTests.ModelProviders;

using Recall.FakeModelProvider;
using Recall.Server.Domain.ModelProviders;
using Recall.Server.Domain.ModelProviders.Clients;
using Shouldly;

/// <summary>Each limit and each fault of an answer, on each adapter, against a server over real HTTP. A limit here is short, so the test is fast.</summary>
public class ProviderLimitTests
{
    private const string Instructions = "Clean this up.";
    private static readonly TimeSpan TestLimit = TimeSpan.FromSeconds(60);

    private static readonly ProviderLimits Short = new()
    {
        Idle = TimeSpan.FromSeconds(1),
        Call = TimeSpan.FromSeconds(30),
        Listing = TimeSpan.FromSeconds(30),
        BodyBytes = 256 * 1024,
        StreamBytes = 1024 * 1024,
        StreamLineBytes = 128 * 1024,
        AnswerCharacters = 64 * 1024,
        ModelPages = 3,
    };

    private sealed class GuardedClients(ProviderLimits limits) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new ProviderReadGuard(limits)
        {
            InnerHandler = new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false, ConnectCallback = ProviderAddressRule.ConnectAsync },
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    private static ModelProviderClient ClientWith(ProviderLimits limits)
    {
        var clients = new GuardedClients(limits);
        return new ModelProviderClient(new AnthropicModelClient(clients, limits), new OpenAiCompatibleClient(clients, limits), limits);
    }

    private static async Task<(FakeModelProvider Fake, ModelEndpoint Endpoint)> StartAsync(ModelProviderKind kind, FakeMode mode, int delayMs = 0, string? reply = null, string contentType = "application/json")
    {
        var key = CleanupScenario.NewKey();
        var fake = await FakeModelProvider.StartAsync(key);
        fake.Behavior.Mode = mode;
        fake.Behavior.DelayMs = delayMs;
        fake.Behavior.Reply = reply;
        fake.Behavior.ContentType = contentType;
        return (fake, new ModelEndpoint(kind, CleanupScenario.BaseUrlOf(fake, kind), key));
    }

    private static Task<ProviderReply<string>> CompleteAsync(ModelProviderClient client, ModelEndpoint endpoint)
        => client.CompleteAsync(endpoint, FakeModelProvider.ModelId, Instructions, "some words", CancellationToken.None).WaitAsync(TestLimit);

    private static ProviderFailure FailureOf<T>(ProviderReply<T> reply) => reply.ShouldBeOfType<ProviderReply<T>.Failed>().Failure;

    [Test]
    [Arguments(ModelProviderKind.Anthropic)]
    [Arguments(ModelProviderKind.OpenAiCompatible)]
    public async Task a_stream_with_no_progress_ends_at_the_idle_limit_and_a_later_try_can_work(ModelProviderKind kind)
    {
        // Arrange
        var (fake, endpoint) = await StartAsync(kind, FakeMode.StallMidStream);
        await using var _ = fake;

        // Act
        var reply = await CompleteAsync(ClientWith(Short), endpoint);

        // Assert
        FailureOf(reply).Kind.ShouldBe(ProviderFailureKind.Unavailable);
    }

    [Test]
    [Arguments(ModelProviderKind.Anthropic)]
    [Arguments(ModelProviderKind.OpenAiCompatible)]
    public async Task a_call_with_no_headers_ends_at_the_idle_limit(ModelProviderKind kind)
    {
        // Arrange
        var (fake, endpoint) = await StartAsync(kind, FakeMode.Hold, delayMs: 120_000);
        await using var _ = fake;
        var limits = new ProviderLimits { Idle = TimeSpan.FromSeconds(1) };

        // Act
        var started = DateTimeOffset.UtcNow;
        var reply = await CompleteAsync(ClientWith(limits), endpoint);

        // Assert
        FailureOf(reply).Kind.ShouldBe(ProviderFailureKind.Unavailable);
        (DateTimeOffset.UtcNow - started).ShouldBeLessThan(TimeSpan.FromSeconds(20));
    }

    [Test]
    [Arguments(ModelProviderKind.Anthropic)]
    [Arguments(ModelProviderKind.OpenAiCompatible)]
    public async Task a_long_stream_that_makes_progress_finishes_past_the_idle_limit(ModelProviderKind kind)
    {
        // Arrange: each word comes within the idle limit, and the whole stream takes three times the limit.
        var (fake, endpoint) = await StartAsync(kind, FakeMode.SlowWords, delayMs: 500);
        await using var _ = fake;

        // Act
        var started = DateTimeOffset.UtcNow;
        var reply = await CompleteAsync(ClientWith(Short), endpoint);

        // Assert
        reply.ShouldBeOfType<ProviderReply<string>.Answered>().Value.ShouldContain("cleanedMarkdown");
        (DateTimeOffset.UtcNow - started).ShouldBeGreaterThan(Short.Idle);
    }

    [Test]
    [Arguments(ModelProviderKind.Anthropic)]
    [Arguments(ModelProviderKind.OpenAiCompatible)]
    public async Task a_stream_of_keep_alives_with_no_words_ends_at_the_call_limit(ModelProviderKind kind)
    {
        // Arrange
        var (fake, endpoint) = await StartAsync(kind, FakeMode.KeepAliveForever, delayMs: 200);
        await using var _ = fake;
        var limits = new ProviderLimits { Idle = TimeSpan.FromSeconds(1), Call = TimeSpan.FromSeconds(3) };

        // Act
        var reply = await CompleteAsync(ClientWith(limits), endpoint);

        // Assert
        FailureOf(reply).Kind.ShouldBe(ProviderFailureKind.Unavailable);
    }

    [Test]
    [Arguments(ModelProviderKind.Anthropic)]
    [Arguments(ModelProviderKind.OpenAiCompatible)]
    public async Task a_stream_that_is_cut_on_the_way_can_work_on_a_later_try(ModelProviderKind kind)
    {
        // Arrange
        var (fake, endpoint) = await StartAsync(kind, FakeMode.CutStream);
        await using var _ = fake;

        // Act
        var reply = await CompleteAsync(ClientWith(Short), endpoint);

        // Assert
        FailureOf(reply).Kind.ShouldBe(ProviderFailureKind.Unavailable);
    }

    [Test]
    [Arguments(ModelProviderKind.Anthropic, "application/json")]
    [Arguments(ModelProviderKind.OpenAiCompatible, "application/json")]
    [Arguments(ModelProviderKind.Anthropic, "text/event-stream")]
    [Arguments(ModelProviderKind.OpenAiCompatible, "text/event-stream")]
    public async Task a_body_with_no_end_ends_at_its_byte_limit_and_will_not_change_on_a_retry(ModelProviderKind kind, string contentType)
    {
        // Arrange
        var (fake, endpoint) = await StartAsync(kind, FakeMode.EndlessBody, contentType: contentType);
        await using var _ = fake;
        var client = ClientWith(Short);

        // Act
        var answer = await CompleteAsync(client, endpoint);
        var list = await client.ListModelsAsync(endpoint, CancellationToken.None).WaitAsync(TestLimit);

        // Assert
        FailureOf(answer).Kind.ShouldBe(ProviderFailureKind.BadOutput);
        FailureOf(list).Kind.ShouldBe(ProviderFailureKind.BadOutput);
    }

    [Test]
    [Arguments(ModelProviderKind.Anthropic)]
    [Arguments(ModelProviderKind.OpenAiCompatible)]
    public async Task a_long_answer_of_valid_events_ends_at_the_limit_of_its_words(ModelProviderKind kind)
    {
        // Arrange
        var (fake, endpoint) = await StartAsync(kind, FakeMode.Normal, reply: new string('w', 100 * 1024));
        await using var _ = fake;

        // Act
        var reply = await CompleteAsync(ClientWith(Short), endpoint);

        // Assert
        FailureOf(reply).Kind.ShouldBe(ProviderFailureKind.BadOutput);
    }

    [Test]
    [Arguments(ModelProviderKind.Anthropic, "{ this is no json", "application/json")]
    [Arguments(ModelProviderKind.OpenAiCompatible, "{ this is no json", "application/json")]
    [Arguments(ModelProviderKind.Anthropic, "<html><body>a page of another service</body></html>", "text/html")]
    [Arguments(ModelProviderKind.OpenAiCompatible, "<html><body>a page of another service</body></html>", "text/html")]
    [Arguments(ModelProviderKind.Anthropic, "{}", "application/json; charset=no-such-charset")]
    [Arguments(ModelProviderKind.OpenAiCompatible, "{}", "application/json; charset=no-such-charset")]
    [Arguments(ModelProviderKind.Anthropic, "data: { this is no json\n\n", "text/event-stream")]
    [Arguments(ModelProviderKind.OpenAiCompatible, "data: { this is no json\n\n", "text/event-stream")]
    [Arguments(ModelProviderKind.Anthropic, "<html>a page in the type of a stream</html>", "text/event-stream")]
    public async Task an_answer_that_will_not_change_on_a_retry_is_bad_output(ModelProviderKind kind, string body, string contentType)
    {
        // Arrange
        var (fake, endpoint) = await StartAsync(kind, FakeMode.RawBody, reply: body, contentType: contentType);
        await using var _ = fake;

        // Act
        var answer = await CompleteAsync(ClientWith(Short), endpoint);

        // Assert
        FailureOf(answer).Kind.ShouldBe(ProviderFailureKind.BadOutput);
    }

    [Test]
    [Arguments(ModelProviderKind.Anthropic, "{ this is no json", "application/json")]
    [Arguments(ModelProviderKind.OpenAiCompatible, "{ this is no json", "application/json")]
    [Arguments(ModelProviderKind.Anthropic, "<html>a page of another service</html>", "text/html")]
    [Arguments(ModelProviderKind.OpenAiCompatible, "<html>a page of another service</html>", "text/html")]
    [Arguments(ModelProviderKind.Anthropic, "{}", "application/json; charset=no-such-charset")]
    [Arguments(ModelProviderKind.OpenAiCompatible, "{}", "application/json; charset=no-such-charset")]
    public async Task a_list_of_models_that_does_not_read_is_a_failure_and_no_exception(ModelProviderKind kind, string body, string contentType)
    {
        // Arrange
        var (fake, endpoint) = await StartAsync(kind, FakeMode.RawBody, reply: body, contentType: contentType);
        await using var _ = fake;

        // Act
        var reply = await ClientWith(Short).ListModelsAsync(endpoint, CancellationToken.None).WaitAsync(TestLimit);

        // Assert
        var failure = FailureOf(reply);
        failure.Kind.ShouldBe(ProviderFailureKind.BadOutput);
        failure.DisplayMessage.ShouldNotContain("another service");
    }

    [Test]
    public async Task an_anthropic_list_that_never_ends_stops_at_the_page_limit()
    {
        // Arrange
        var (fake, endpoint) = await StartAsync(ModelProviderKind.Anthropic, FakeMode.ModelsWithNoEnd);
        await using var _ = fake;

        // Act
        var reply = await ClientWith(Short).ListModelsAsync(endpoint, CancellationToken.None).WaitAsync(TestLimit);

        // Assert
        reply.ShouldBeOfType<ProviderReply<IReadOnlyList<ProviderModel>>.Answered>().Value.Count.ShouldBe(Short.ModelPages);
        fake.ModelListCalls.ShouldBe(Short.ModelPages);
    }

    [Test]
    public async Task a_model_id_with_bad_bytes_or_a_hidden_character_is_not_offered()
    {
        // Arrange: \ufffd is what a reader makes of bad UTF-8.
        const string body = """{"data":[{"id":"good-model"},{"id":"bad\ufffdmodel"},{"id":"nul\u0000model"},{"id":"bidi\u202emodel"}]}""";
        var (fake, endpoint) = await StartAsync(ModelProviderKind.OpenAiCompatible, FakeMode.RawBody, reply: body);
        await using var _ = fake;

        // Act
        var reply = await ClientWith(Short).ListModelsAsync(endpoint, CancellationToken.None).WaitAsync(TestLimit);

        // Assert
        reply.ShouldBeOfType<ProviderReply<IReadOnlyList<ProviderModel>>.Answered>().Value.Select(model => model.Id).ShouldBe(["good-model"]);
    }

    [Test]
    [Arguments(ModelProviderKind.Anthropic)]
    [Arguments(ModelProviderKind.OpenAiCompatible)]
    public async Task a_refused_address_is_refused_and_the_failure_is_a_fixed_text(ModelProviderKind kind)
    {
        // Arrange
        var endpoint = new ModelEndpoint(kind, kind == ModelProviderKind.Anthropic ? "http://169.254.169.254" : "http://169.254.169.254/v1", "key");

        // Act
        var reply = await ClientWith(Short).ListModelsAsync(endpoint, CancellationToken.None).WaitAsync(TestLimit);

        // Assert
        var failure = FailureOf(reply);
        failure.Kind.ShouldBe(ProviderFailureKind.Refused);
        failure.DisplayMessage.ShouldBe(ProviderFailureText.AddressRefused().DisplayMessage);
    }
}
