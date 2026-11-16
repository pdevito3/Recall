namespace Recall.FunctionalTests.FunctionalTests.ModelProviders;

using System.Text;
using System.Text.Json;
using Recall.FakeModelProvider;
using Recall.Server.Domain.ModelProviders;
using Recall.Server.Domain.ModelProviders.Clients;
using Recall.Server.Domain.Sessions;
using Recall.Server.Domain.Sessions.Cleanup;
using Shouldly;

/// <summary>Each limit and each fault of an answer, on each adapter, against a server over real HTTP. A limit here is short, so the test is fast.</summary>
public class ProviderLimitTests
{
    private const string Instructions = "Clean this up.";
    private static readonly TimeSpan TestLimit = TimeSpan.FromSeconds(60);
    private static readonly string[] ShortKeyModels = ["llama3.1:8b", "mixtral:8x7b", "gemma3:12b"];

    private static readonly ProviderLimits Short = new()
    {
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
        var limits = Short with { Idle = TimeSpan.FromSeconds(1) };

        // Act
        var started = DateTimeOffset.UtcNow;
        var reply = await CompleteAsync(ClientWith(limits), endpoint);

        // Assert: the call limit of these limits is 30 s, so an end before it is the idle limit.
        FailureOf(reply).Kind.ShouldBe(ProviderFailureKind.Unavailable);
        (DateTimeOffset.UtcNow - started).ShouldBeLessThan(TimeSpan.FromSeconds(20));
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
        var limits = Short with { Idle = TimeSpan.FromSeconds(1) };

        // Act
        var started = DateTimeOffset.UtcNow;
        var reply = await CompleteAsync(ClientWith(limits), endpoint);

        // Assert
        reply.ShouldBeOfType<ProviderReply<string>.Answered>().Value.ShouldContain("cleanedMarkdown");
        (DateTimeOffset.UtcNow - started).ShouldBeGreaterThan(limits.Idle);
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
    public async Task a_stream_of_pings_ends_at_the_no_words_limit_in_one_call_and_no_later_try(ModelProviderKind kind)
    {
        // Arrange
        var (fake, endpoint) = await StartAsync(kind, FakeMode.KeepAliveForever, delayMs: 100);
        await using var _ = fake;
        var limits = new ProviderLimits { Idle = TimeSpan.FromSeconds(5), NoWords = TimeSpan.FromSeconds(1), Call = TimeSpan.FromSeconds(50) };

        // Act
        var started = DateTimeOffset.UtcNow;
        var reply = await CompleteAsync(ClientWith(limits), endpoint);

        // Assert
        FailureOf(reply).Kind.ShouldBe(ProviderFailureKind.NoWords);
        (DateTimeOffset.UtcNow - started).ShouldBeLessThan(TimeSpan.FromSeconds(20));
        CleanupAttempt.AfterFailure(FailureOf(reply), attempt: 1).ShouldBe(new CleanupStep.Fail(CleanupFailure.ProviderUnavailable));
    }

    [Test]
    [Arguments(ModelProviderKind.Anthropic, FakeMode.SlowWords, 500)]
    [Arguments(ModelProviderKind.OpenAiCompatible, FakeMode.SlowWords, 500)]
    [Arguments(ModelProviderKind.Anthropic, FakeMode.Thinking, 300)]
    [Arguments(ModelProviderKind.OpenAiCompatible, FakeMode.Thinking, 300)]
    public async Task a_stream_of_words_or_thoughts_finishes_past_the_no_words_limit(ModelProviderKind kind, FakeMode mode, int delayMs)
    {
        // Arrange
        var (fake, endpoint) = await StartAsync(kind, mode, delayMs: delayMs);
        await using var _ = fake;
        var limits = new ProviderLimits { Idle = TimeSpan.FromSeconds(5), NoWords = TimeSpan.FromSeconds(1), Call = TimeSpan.FromSeconds(50) };

        // Act
        var started = DateTimeOffset.UtcNow;
        var reply = await CompleteAsync(ClientWith(limits), endpoint);

        // Assert
        reply.ShouldBeOfType<ProviderReply<string>.Answered>().Value.ShouldContain("cleanedMarkdown");
        (DateTimeOffset.UtcNow - started).ShouldBeGreaterThan(limits.NoWords);
    }

    private const string AnthropicWordsEvents = """
        event: content_block_delta
        data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"WORDS"}}

        event: message_delta
        data: {"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"output_tokens":5}}


        """;

    private const string CompatibleWordsEvents = """
        data: {"choices":[{"index":0,"delta":{"content":"WORDS"},"finish_reason":null}]}

        data: {"choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}

        data: [DONE]


        """;

    private const string CompatibleWordsObject = """{"choices":[{"index":0,"message":{"role":"assistant","content":"WORDS"},"finish_reason":"stop"}]}""";

    private static byte[] WithBadByte(string body)
    {
        var parts = body.Split("WORDS");
        return [.. Encoding.UTF8.GetBytes(parts[0]), .. "caf"u8.ToArray(), 0xE9, .. "bon"u8.ToArray(), .. Encoding.UTF8.GetBytes(parts[1])];
    }

    public static IEnumerable<Func<(ModelProviderKind Kind, string Body, string ContentType)>> EachPathOfAnAnswer()
    {
        (ModelProviderKind, string, string)[] paths =
        [
            (ModelProviderKind.Anthropic, AnthropicWordsEvents, "text/event-stream"),
            (ModelProviderKind.OpenAiCompatible, CompatibleWordsEvents, "text/event-stream"),
            (ModelProviderKind.OpenAiCompatible, CompatibleWordsObject, "application/json"),
        ];
        return paths.Select<(ModelProviderKind, string, string), Func<(ModelProviderKind, string, string)>>(path => () => path);
    }

    [Test]
    [MethodDataSource(nameof(EachPathOfAnAnswer))]
    public async Task a_byte_that_is_not_utf8_in_an_answer_ends_as_bad_output_in_one_call((ModelProviderKind Kind, string Body, string ContentType) path)
    {
        // Arrange
        var (fake, endpoint) = await StartAsync(path.Kind, FakeMode.RawBody, contentType: path.ContentType);
        await using var _ = fake;
        fake.Behavior.ReplyBytes = WithBadByte(path.Body);
        fake.Behavior.RawBodyOnWordsOnly = true;

        // Act
        var reply = await CompleteAsync(ClientWith(Short), endpoint);

        // Assert
        FailureOf(reply).Kind.ShouldBe(ProviderFailureKind.BadOutput);
        fake.CompletionCalls.ShouldBe(1);
    }

    [Test]
    [MethodDataSource(nameof(EachPathOfAnAnswer))]
    public async Task a_character_that_crosses_each_read_stays_whole((ModelProviderKind Kind, string Body, string ContentType) path)
    {
        // Arrange: a person can also get U+FFFD from a model as a word of its own.
        const string words = "caf\u00E9 \u20AC \U0001F600 \uFFFD";
        var (fake, endpoint) = await StartAsync(path.Kind, FakeMode.RawBody, contentType: path.ContentType);
        await using var _ = fake;
        fake.Behavior.ReplyBytes = Encoding.UTF8.GetBytes(path.Body.Replace("WORDS", words, StringComparison.Ordinal));
        fake.Behavior.RawBodyOnWordsOnly = true;

        // Act
        var reply = await CompleteAsync(ClientWith(Short), endpoint);

        // Assert
        reply.ShouldBeOfType<ProviderReply<string>.Answered>().Value.ShouldBe(words);
    }

    [Test]
    [Arguments(ModelProviderKind.Anthropic)]
    [Arguments(ModelProviderKind.OpenAiCompatible)]
    public async Task a_byte_that_is_not_utf8_in_a_list_of_models_gives_no_empty_list(ModelProviderKind kind)
    {
        // Arrange
        var (fake, endpoint) = await StartAsync(kind, FakeMode.RawBody, contentType: "application/json");
        await using var _ = fake;
        fake.Behavior.ReplyBytes = WithBadByte("""{"data":[{"id":"WORDS","display_name":"x","type":"model","created_at":"2026-01-01T00:00:00Z"}],"has_more":false,"first_id":null,"last_id":null}""");

        // Act
        var reply = await ClientWith(Short).ListModelsAsync(endpoint, CancellationToken.None).WaitAsync(TestLimit);

        // Assert
        FailureOf(reply).Kind.ShouldBe(ProviderFailureKind.BadOutput);
    }

    [Test]
    public async Task a_byte_that_is_not_utf8_in_an_error_keeps_the_status_of_the_error()
    {
        // Arrange
        var (fake, endpoint) = await StartAsync(ModelProviderKind.OpenAiCompatible, FakeMode.RawBody, contentType: "application/json");
        await using var _ = fake;
        fake.Behavior.ReplyBytes = WithBadByte("""{"error":{"message":"WORDS","code":1}}""");
        fake.Behavior.RawStatus = 401;

        // Act
        var reply = await CompleteAsync(ClientWith(Short), endpoint);

        // Assert
        var failure = FailureOf(reply);
        failure.Kind.ShouldBe(ProviderFailureKind.Refused);
        failure.StatusCode.ShouldBe(401);
    }

    [Test]
    [Arguments(ModelProviderKind.Anthropic)]
    [Arguments(ModelProviderKind.OpenAiCompatible)]
    public async Task a_200_stream_with_no_event_and_a_clean_close_ends_as_bad_output_in_one_call(ModelProviderKind kind)
    {
        // Arrange
        var (fake, endpoint) = await StartAsync(kind, FakeMode.RawBody, reply: ": just a comment\n\n", contentType: "text/event-stream");
        await using var _ = fake;
        fake.Behavior.RawBodyOnWordsOnly = true;

        // Act
        var reply = await CompleteAsync(ClientWith(Short), endpoint);

        // Assert
        FailureOf(reply).Kind.ShouldBe(ProviderFailureKind.BadOutput);
        fake.CompletionCalls.ShouldBe(1);
    }

    [Test]
    public async Task a_key_as_an_id_of_a_model_does_not_reach_the_list()
    {
        // Arrange
        var (fake, endpoint) = await StartAsync(ModelProviderKind.OpenAiCompatible, FakeMode.RawBody, contentType: "application/json");
        await using var _ = fake;
        var key = endpoint.ApiKey!;
        var bytes = Encoding.UTF8.GetBytes(key);
        string[] forms = [key, key.ToUpperInvariant(), key[..12], Convert.ToBase64String(bytes), Convert.ToHexString(bytes)];
        fake.Behavior.Reply = JsonSerializer.Serialize(new { data = forms.Append("good-model").Select(id => new { id }) });

        // Act
        var reply = await ClientWith(Short).ListModelsAsync(endpoint, CancellationToken.None).WaitAsync(TestLimit);

        // Assert
        reply.ShouldBeOfType<ProviderReply<IReadOnlyList<ProviderModel>>.Answered>().Value.Select(model => model.Id).ShouldBe(["good-model"]);
    }

    [Test]
    public async Task a_short_placeholder_key_hides_no_model()
    {
        // Arrange
        var fake = await FakeModelProvider.StartAsync("a");
        await using var _ = fake;
        fake.Behavior.Mode = FakeMode.RawBody;
        fake.Behavior.Reply = JsonSerializer.Serialize(new { data = ShortKeyModels.Select(id => new { id }) });
        var endpoint = new ModelEndpoint(ModelProviderKind.OpenAiCompatible, CleanupScenario.BaseUrlOf(fake, ModelProviderKind.OpenAiCompatible), "a");

        // Act
        var reply = await ClientWith(Short).ListModelsAsync(endpoint, CancellationToken.None).WaitAsync(TestLimit);

        // Assert
        reply.ShouldBeOfType<ProviderReply<IReadOnlyList<ProviderModel>>.Answered>().Value.Select(model => model.Id).ShouldBe(ShortKeyModels);
    }

    [Test]
    public async Task a_key_as_a_display_name_gives_way_to_the_id()
    {
        // Arrange
        var (fake, endpoint) = await StartAsync(ModelProviderKind.Anthropic, FakeMode.RawBody, contentType: "application/json");
        await using var _ = fake;
        string[] names = [endpoint.ApiKey!, Convert.ToBase64String(Encoding.UTF8.GetBytes(endpoint.ApiKey!)), new string('n', ModelProvider.ModelMaxLength + 1), "Good name"];
        var rows = names.Select((name, index) => new { type = "model", id = $"model-{index}", display_name = name, created_at = "2026-01-01T00:00:00Z" });
        fake.Behavior.Reply = JsonSerializer.Serialize(new { data = rows, has_more = false, first_id = "model-0", last_id = "model-3" });

        // Act
        var reply = await ClientWith(Short).ListModelsAsync(endpoint, CancellationToken.None).WaitAsync(TestLimit);

        // Assert
        reply.ShouldBeOfType<ProviderReply<IReadOnlyList<ProviderModel>>.Answered>().Value.Select(model => model.DisplayName).ShouldBe(["model-0", "model-1", "model-2", "Good name"]);
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
