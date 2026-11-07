namespace Recall.FakeModelProvider;

using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;

/// <summary>What the fake does with the next request for words. The list of models always follows <see cref="FakeBehavior.Status"/>.</summary>
public enum FakeMode
{
    Normal,

    /// <summary>Answers an error with a body that holds the words and the key of the request, as a real provider can.</summary>
    EchoError,

    /// <summary>Answers an error with <see cref="FakeBehavior.Status"/>.</summary>
    Status,

    /// <summary>Waits for <see cref="FakeBehavior.DelayMs"/> before the answer. A closed connection ends the wait.</summary>
    Hold,

    /// <summary>An Anthropic stream that sends its first events and then nothing, as a connection whose route dropped does.</summary>
    StallMidStream,

    /// <summary>A 200 with a cut answer: <c>max_tokens</c> or <c>length</c>.</summary>
    Truncate,

    /// <summary>A 200 with a refusal: <c>refusal</c> or <c>content_filter</c>.</summary>
    Refusal,

    /// <summary>A 200 with words that are no JSON object.</summary>
    BadJson,

    /// <summary>A redirect to <c>/stolen</c>. A client that follows it sends the key there.</summary>
    Redirect,

    /// <summary>A stream with an error after the 200. The error holds the words of the request.</summary>
    MidStreamError,

    /// <summary>A 200 with <see cref="FakeBehavior.Reply"/> as its whole body and <see cref="FakeBehavior.ContentType"/>, on each route.</summary>
    RawBody,

    /// <summary>A 200 with bytes and no end, on each route, in the type <see cref="FakeBehavior.ContentType"/>.</summary>
    EndlessBody,

    /// <summary>A stream that sends one keep-alive and one delta of a space each <see cref="FakeBehavior.DelayMs"/> and no word, with no end.</summary>
    KeepAliveForever,

    /// <summary>A stream of one thought each <see cref="FakeBehavior.DelayMs"/>, ten times, and then the whole answer. A model that thinks looks so.</summary>
    Thinking,

    /// <summary>A stream of one word each <see cref="FakeBehavior.DelayMs"/>, and then the whole answer. A model that works looks so.</summary>
    SlowWords,

    /// <summary>The Anthropic list of models says <c>has_more</c> on each page.</summary>
    ModelsWithNoEnd,

    /// <summary>A stream that ends with no stop reason, as a connection that closes on the way does.</summary>
    CutStream,
}

public sealed class FakeBehavior
{
    public FakeMode Mode { get; set; } = FakeMode.Normal;

    public int Status { get; set; } = 500;

    public int DelayMs { get; set; }

    /// <summary>The words of the answer in place of the object that the fake makes from the request.</summary>
    public string? Reply { get; set; }

    /// <summary>The body of <see cref="FakeMode.RawBody"/> as bytes, one write for each byte, so a character crosses each edge of a read.</summary>
    public byte[]? ReplyBytes { get; set; }

    /// <summary>The status of <see cref="FakeMode.RawBody"/>.</summary>
    public int RawStatus { get; set; } = 200;

    /// <summary><see cref="FakeMode.RawBody"/> on the route of a completion only, so an adapter that reads the model first gets to its stream.</summary>
    public bool RawBodyOnWordsOnly { get; set; }

    public string ContentType { get; set; } = "application/json";

    /// <summary>With <see cref="FakeMode.Hold"/>, only a request that holds these words waits.</summary>
    public string? HoldOnly { get; set; }

    /// <summary>The largest audio file that the route of transcriptions takes. OpenAI and Groq document 25 MB.</summary>
    public long MaxAudioBytes { get; set; } = FakeModelProvider.AudioFileLimit;

    /// <summary>With <see cref="FakeMode.Status"/> or <see cref="FakeMode.Hold"/> on audio, only the first this many audio calls take the mode, and the rest answer.</summary>
    public int? AudioFaultCalls { get; set; }

    /// <summary>
    /// The output limit of a model such as <c>gpt-4o-transcribe</c>, which documents 2,000 tokens. The fake speaks
    /// <see cref="AudioTokensPerSecond"/>, bills tokens, and cuts the text of a part that needs more.
    /// </summary>
    public int? AudioOutputTokens { get; set; }

    /// <summary>OpenAI refuses a file under 0.1 s with a 400.</summary>
    public double MinAudioSeconds { get; set; } = 0.1;

    /// <summary>The output tokens that a second of audio needs. A higher value stands for denser speech.</summary>
    public double AudioTokensPerSecond { get; set; } = FakeModelProvider.TokensPerSecond;
}

/// <summary>
/// A local server that speaks the wire formats of a model provider: the Anthropic API under <c>/v1</c>, and an OpenAI-compatible
/// API under <c>/compat/v1</c>. It checks what the real services check, so a client that sends the wrong request fails here.
/// </summary>
public sealed class FakeModelProvider : IAsyncDisposable
{
    public const string AnthropicVersionHeader = "anthropic-version";
    public const string ModelId = "fake-model-1";
    public const string SecondModelId = "fake-model-2";
    public const long ModelMaxTokens = 64_000;
    public const long AudioFileLimit = 25_000_000;
    public const string TranscriptionModelId = "fake-transcribe-1";

    private static readonly string[] AudioFormats = ["json", "text", "srt", "verbose_json", "vtt"];

    private const int ThoughtsOfAModel = 10;

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly WebApplication _app;
    private readonly string _apiKey;
    private int _completionCalls;
    private int _modelListCalls;
    private int _stolenCalls;
    private int _audioCalls;

    private FakeModelProvider(WebApplication app, string apiKey)
    {
        _app = app;
        _apiKey = apiKey;
    }

    public FakeBehavior Behavior { get; private set; } = new();

    public int CompletionCalls => Volatile.Read(ref _completionCalls);

    public int ModelListCalls => Volatile.Read(ref _modelListCalls);

    public int StolenCalls => Volatile.Read(ref _stolenCalls);

    public int AudioCalls => Volatile.Read(ref _audioCalls);

    /// <summary>Each audio file that the route of transcriptions took, in the order of arrival.</summary>
    public ConcurrentQueue<byte[]> AudioFiles { get; } = new();

    /// <summary>The <c>model</c> field of each audio file that the route took, in the same order.</summary>
    public ConcurrentQueue<string> AudioModels { get; } = new();

    /// <summary>Fast speech: about 250 words a minute at about 1.4 tokens a word.</summary>
    public const double TokensPerSecond = 6;

    private static readonly string[] DefaultTopics = ["work"];
    private static readonly string[] DefaultMoods = ["calm"];

    public ConcurrentQueue<string> RequestBodies { get; } = new();

    public string Address => _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();

    public string AnthropicBaseUrl => Address;

    public string CompatibleBaseUrl => $"{Address}/compat/v1";

    public static async Task<FakeModelProvider> StartAsync(string apiKey, string url = "http://127.0.0.1:0")
    {
        var builder = WebApplication.CreateSlimBuilder();

        // The test folder holds the appsettings.json of the server, whose Kestrel section would take this port.
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection();
        builder.WebHost.UseUrls(url);
        builder.Logging.ClearProviders();
        var app = builder.Build();
        var fake = new FakeModelProvider(app, apiKey);
        fake.MapRoutes();
        await app.StartAsync();
        return fake;
    }

    /// <summary>Ends when the process gets a stop signal.</summary>
    public Task WaitForStopAsync() => _app.WaitForShutdownAsync();

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private void MapRoutes()
    {
        _app.Use(async (context, next) =>
        {
            if (!await WroteBodyOfModeAsync(context))
                await next(context);
        });
        _app.MapGet("/v1/models", AnthropicModels);
        _app.MapGet("/v1/models/{id}", AnthropicModel);
        _app.MapPost("/v1/messages", AnthropicMessage);
        _app.MapGet("/compat/v1/models", CompatibleModels);
        _app.MapPost("/compat/v1/chat/completions", CompatibleCompletion);
        _app.MapPost("/compat/v1/audio/transcriptions", CompatibleTranscription);
        _app.MapGet("/stolen", _ =>
        {
            Interlocked.Increment(ref _stolenCalls);
            return Task.CompletedTask;
        });
        _app.MapPost("/stolen", _ =>
        {
            Interlocked.Increment(ref _stolenCalls);
            return Task.CompletedTask;
        });

        // The lanes drive the fake from outside its process with these.
        _app.MapPost("/control", async (HttpContext context) =>
        {
            Behavior = await context.Request.ReadFromJsonAsync<FakeBehavior>(Web) ?? new FakeBehavior();
            await context.Response.WriteAsJsonAsync(new { Behavior.Mode, Behavior.Status, Behavior.DelayMs });
        });
        _app.MapGet("/control", (HttpContext context) => context.Response.WriteAsJsonAsync(new { completionCalls = CompletionCalls, modelListCalls = ModelListCalls, stolenCalls = StolenCalls, audioCalls = AudioCalls }));
    }

    private async Task<bool> WroteBodyOfModeAsync(HttpContext context)
    {
        if (Behavior.Mode is not (FakeMode.RawBody or FakeMode.EndlessBody) || context.Request.Path.StartsWithSegments("/control"))
            return false;

        if (Behavior.RawBodyOnWordsOnly && context.Request.Path.Value!.Contains("/models", StringComparison.Ordinal))
            return false;

        if (context.Request.Path.Value!.EndsWith("/models", StringComparison.Ordinal))
            Interlocked.Increment(ref _modelListCalls);
        else
            Interlocked.Increment(ref _completionCalls);

        context.Response.StatusCode = Behavior.RawStatus;
        context.Response.Headers.ContentType = Behavior.ContentType;
        if (Behavior.Mode == FakeMode.RawBody)
        {
            if (Behavior.ReplyBytes is { } bytes)
            {
                for (var at = 0; at < bytes.Length; at++)
                {
                    await context.Response.Body.WriteAsync(bytes.AsMemory(at, 1), context.RequestAborted);
                    await context.Response.Body.FlushAsync(context.RequestAborted);
                }

                return true;
            }

            await context.Response.WriteAsync(Behavior.Reply ?? string.Empty);
            return true;
        }

        var block = new byte[64 * 1024];
        Array.Fill(block, (byte)'x');
        while (!context.RequestAborted.IsCancellationRequested)
            await context.Response.Body.WriteAsync(block, context.RequestAborted);
        return true;
    }

    private async Task AnthropicModels(HttpContext context)
    {
        Interlocked.Increment(ref _modelListCalls);
        if (await RefusedAnthropicAsync(context))
            return;

        // The API takes "limit" from 1 to 1000, and it refuses any other value.
        if (context.Request.Query["limit"] is { Count: > 0 } limit && int.Parse(limit[0]!, System.Globalization.CultureInfo.InvariantCulture) is < 1 or > 1000)
        {
            await WriteAnthropicErrorAsync(context, 400, "invalid_request_error", "limit: must be between 1 and 1000");
            return;
        }

        if (Behavior.Mode == FakeMode.ModelsWithNoEnd)
        {
            var id = $"page-model-{ModelListCalls}";
            await context.Response.WriteAsJsonAsync(new { data = new[] { AnthropicModelBody(id, id) }, has_more = true, first_id = id, last_id = id });
            return;
        }

        await context.Response.WriteAsJsonAsync(new
        {
            data = new[] { AnthropicModelBody(ModelId, "Fake Model One"), AnthropicModelBody(SecondModelId, "Fake Model Two") },
            has_more = false,
            first_id = ModelId,
            last_id = SecondModelId,
        });
    }

    private async Task AnthropicModel(HttpContext context)
    {
        if (await RefusedAnthropicAsync(context))
            return;

        var id = context.Request.RouteValues["id"]?.ToString() ?? string.Empty;
        if (id is not (ModelId or SecondModelId))
        {
            await WriteAnthropicErrorAsync(context, 404, "not_found_error", "The model does not exist.");
            return;
        }

        await context.Response.WriteAsJsonAsync(AnthropicModelBody(id, id));
    }

    private static object AnthropicModelBody(string id, string name) => new
    {
        type = "model",
        id,
        display_name = name,
        created_at = "2026-01-01T00:00:00Z",
        max_tokens = ModelMaxTokens,
        max_input_tokens = 200_000,
        lifecycle = "active",
    };

    private async Task AnthropicMessage(HttpContext context)
    {
        var body = await ReadBodyAsync(context);
        Interlocked.Increment(ref _completionCalls);
        RequestBodies.Enqueue(body);
        if (await RefusedAnthropicAsync(context))
            return;

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (root.TryGetProperty("temperature", out _))
        {
            await WriteAnthropicErrorAsync(context, 400, "invalid_request_error", "temperature is not supported in this API version.");
            return;
        }

        if (!root.TryGetProperty("max_tokens", out var maxTokens) || maxTokens.GetInt64() > ModelMaxTokens)
        {
            await WriteAnthropicErrorAsync(context, 400, "invalid_request_error", "max_tokens is above the limit of this model.");
            return;
        }

        var text = UserTextOfAnthropic(root);
        switch (Behavior.Mode)
        {
            case FakeMode.EchoError:
                await WriteAnthropicErrorAsync(context, 400, "invalid_request_error", $"Cannot read this input: {text} with key {_apiKey}");
                return;
            case FakeMode.Status:
                await WriteAnthropicErrorAsync(context, Behavior.Status, "api_error", $"The fake answers with this status while it reads: {text} with key {_apiKey}");
                return;
            case FakeMode.Redirect:
                context.Response.StatusCode = 307;
                context.Response.Headers.Location = $"{Address}/stolen";
                return;
            case FakeMode.Hold when Behavior.HoldOnly is null || text.Contains(Behavior.HoldOnly, StringComparison.Ordinal):
                await Task.Delay(Behavior.DelayMs, context.RequestAborted);
                break;
        }

        var stopReason = Behavior.Mode switch
        {
            FakeMode.Truncate => "max_tokens",
            FakeMode.Refusal => "refusal",
            _ => "end_turn",
        };
        await WriteAnthropicStreamAsync(context, ReplyFor(text), stopReason, text);
    }

    private async Task WriteAnthropicStreamAsync(HttpContext context, string reply, string stopReason, string requestText)
    {
        context.Response.ContentType = "text/event-stream";
        await WriteEventAsync(context, "message_start", new
        {
            type = "message_start",
            message = new
            {
                id = "msg_fake",
                type = "message",
                role = "assistant",
                content = Array.Empty<object>(),
                model = ModelId,
                stop_reason = (string?)null,
                stop_sequence = (string?)null,
                usage = new { input_tokens = 1, output_tokens = 1 },
            },
        });
        // The API sends these among the rest of a stream, and it can add a new type of event at any time.
        await WriteEventAsync(context, "ping", new { type = "ping" });
        await WriteEventAsync(context, "new_kind_of_event", new { type = "new_kind_of_event", detail = "skip this" });
        await WriteEventAsync(context, "content_block_start", new { type = "content_block_start", index = 0, content_block = new { type = "thinking", thinking = string.Empty, signature = string.Empty } });
        await WriteEventAsync(context, "content_block_delta", new { type = "content_block_delta", index = 0, delta = new { type = "thinking_delta", thinking = "The fake thinks, and this is no part of the answer." } });
        await WriteEventAsync(context, "content_block_stop", new { type = "content_block_stop", index = 0 });
        await WriteEventAsync(context, "content_block_start", new { type = "content_block_start", index = 1, content_block = new { type = "text", text = string.Empty } });
        if (Behavior.Mode == FakeMode.StallMidStream)
        {
            await Task.Delay(Timeout.Infinite, context.RequestAborted);
            return;
        }

        if (Behavior.Mode == FakeMode.MidStreamError)
        {
            await WriteEventAsync(context, "error", new { type = "error", error = new { type = "overloaded_error", message = $"Stopped at: {requestText} with key {_apiKey}" } });
            return;
        }

        if (Behavior.Mode == FakeMode.KeepAliveForever)
        {
            while (true)
            {
                await Task.Delay(Behavior.DelayMs, context.RequestAborted);
                await WriteEventAsync(context, "ping", new { type = "ping" });
                await WriteEventAsync(context, "content_block_delta", new { type = "content_block_delta", index = 1, delta = new { type = "text_delta", text = " " } });
            }
        }

        if (Behavior.Mode == FakeMode.Thinking)
        {
            for (var thought = 0; thought < ThoughtsOfAModel; thought++)
            {
                await Task.Delay(Behavior.DelayMs, context.RequestAborted);
                await WriteEventAsync(context, "content_block_delta", new { type = "content_block_delta", index = 0, delta = new { type = "thinking_delta", thinking = "hmm" } });
            }
        }

        foreach (var part in PartsOf(reply))
        {
            await WriteEventAsync(context, "content_block_delta", new { type = "content_block_delta", index = 1, delta = new { type = "text_delta", text = part } });
            if (Behavior.Mode == FakeMode.SlowWords)
                await Task.Delay(Behavior.DelayMs, context.RequestAborted);
        }

        if (Behavior.Mode == FakeMode.CutStream)
            return;

        await WriteEventAsync(context, "content_block_stop", new { type = "content_block_stop", index = 1 });
        await WriteEventAsync(context, "message_delta", new
        {
            type = "message_delta",
            delta = new { stop_reason = stopReason, stop_sequence = (string?)null },
            usage = new { output_tokens = 5 },
        });
        await WriteEventAsync(context, "message_stop", new { type = "message_stop" });
    }

    private IEnumerable<string> PartsOf(string reply)
        => Behavior.Mode == FakeMode.SlowWords ? reply.Chunk(Math.Max(1, reply.Length / 6)).Select(part => new string(part)) : [reply];

    private static async Task WriteEventAsync(HttpContext context, string name, object data)
    {
        await context.Response.WriteAsync($"event: {name}\ndata: {JsonSerializer.Serialize(data)}\n\n");
        await context.Response.Body.FlushAsync();
    }

    private async Task<bool> RefusedAnthropicAsync(HttpContext context)
    {
        if (!context.Request.Headers.ContainsKey(AnthropicVersionHeader))
        {
            await WriteAnthropicErrorAsync(context, 400, "invalid_request_error", "anthropic-version: header is required");
            return true;
        }

        // The API takes "Authorization: Bearer", and "x-api-key" as its legacy form. The official SDK sends the first.
        if (context.Request.Headers.Authorization != $"Bearer {_apiKey}" && context.Request.Headers["x-api-key"] != _apiKey)
        {
            await WriteAnthropicErrorAsync(context, 401, "authentication_error", "invalid x-api-key");
            return true;
        }

        return false;
    }

    private static Task WriteAnthropicErrorAsync(HttpContext context, int status, string type, string message)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(new { type = "error", error = new { type, message }, request_id = "req_fake" });
    }

    private async Task CompatibleModels(HttpContext context)
    {
        Interlocked.Increment(ref _modelListCalls);
        if (await RefusedCompatibleAsync(context))
            return;

        await context.Response.WriteAsJsonAsync(new
        {
            @object = "list",
            data = new[]
            {
                new { id = ModelId, @object = "model", created = 1_700_000_000, owned_by = "fake" },
                new { id = SecondModelId, @object = "model", created = 1_700_000_000, owned_by = "fake" },
            },
        });
    }

    /// <summary>
    /// <c>POST /v1/audio/transcriptions</c> of the OpenAI reference: a multipart form with <c>file</c> and <c>model</c>, a file of
    /// up to 25 MB, and a <c>response_format</c> from its list. The answer to <c>json</c> is <c>{"text": ...}</c> with the usage.
    /// The text names the part and its length, so a test reads the order of the parts from the stored text.
    /// </summary>
    private async Task CompatibleTranscription(HttpContext context)
    {
        var call = Interlocked.Increment(ref _audioCalls);
        if (await RefusedCompatibleAsync(context))
            return;

        if (!context.Request.HasFormContentType)
        {
            await WriteCompatibleErrorAsync(context, 400, "The request must be multipart/form-data.");
            return;
        }

        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        var file = form.Files["file"];
        if (file is null || string.IsNullOrWhiteSpace(form["model"]))
        {
            await WriteCompatibleErrorAsync(context, 400, "you must provide a file and a model");
            return;
        }

        if (file.Length > Behavior.MaxAudioBytes)
        {
            await WriteCompatibleErrorAsync(context, 413, $"Maximum content size limit ({Behavior.MaxAudioBytes}) exceeded ({file.Length} bytes read)");
            return;
        }

        var format = form["response_format"].ToString();
        if (format.Length > 0 && !AudioFormats.Contains(format))
        {
            await WriteCompatibleErrorAsync(context, 400, $"response_format must be one of {string.Join(", ", AudioFormats)}");
            return;
        }

        using var bytes = new MemoryStream();
        await file.CopyToAsync(bytes, context.RequestAborted);
        AudioFiles.Enqueue(bytes.ToArray());
        AudioModels.Enqueue(form["model"].ToString());
        if (Math.Max(0, bytes.Length - 44) / 2 / 16_000.0 < Behavior.MinAudioSeconds)
        {
            await WriteCompatibleErrorAsync(context, 400, $"Audio file is too short. Minimum audio length is {Behavior.MinAudioSeconds} seconds.");
            return;
        }

        var faulted = Behavior.AudioFaultCalls is not { } faults || call <= faults;
        switch (Behavior.Mode)
        {
            case FakeMode.EchoError when faulted:
                await WriteCompatibleErrorAsync(context, 400, $"Cannot read this audio with key {_apiKey}");
                return;
            case FakeMode.Status when faulted:
                await WriteCompatibleErrorAsync(context, Behavior.Status, $"The fake answers with this status, key {_apiKey}");
                return;
            case FakeMode.Hold when faulted:
                await Task.Delay(Behavior.DelayMs, context.RequestAborted);
                break;
            case FakeMode.BadJson when faulted:
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync("{\"words\": ");
                return;
        }

        var seconds = Math.Max(0, bytes.Length - 44) / 2 / 16_000.0;
        var text = Behavior.Reply ?? $"Part {AudioFiles.Count} heard {Seconds(seconds)} seconds.";
        if (Behavior.AudioOutputTokens is not { } limit)
        {
            await context.Response.WriteAsJsonAsync(new { text, usage = new { type = "duration", seconds = (int)Math.Ceiling(seconds) } });
            return;
        }

        var tokens = (int)Math.Ceiling(seconds * Behavior.AudioTokensPerSecond);
        if (tokens > limit)
        {
            tokens = limit;
            text = $"Part {AudioFiles.Count} heard {Seconds(limit / Behavior.AudioTokensPerSecond)} of {Seconds(seconds)} seconds";
        }

        await context.Response.WriteAsJsonAsync(new { text, usage = new { type = "tokens", input_tokens = (int)Math.Ceiling(seconds * 10), output_tokens = tokens, total_tokens = tokens } });
    }

    private static string Seconds(double seconds) => seconds.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);

    private async Task CompatibleCompletion(HttpContext context)
    {
        var body = await ReadBodyAsync(context);
        Interlocked.Increment(ref _completionCalls);
        RequestBodies.Enqueue(body);
        if (await RefusedCompatibleAsync(context))
            return;

        using var document = JsonDocument.Parse(body);
        var text = UserTextOfCompatible(document.RootElement);
        switch (Behavior.Mode)
        {
            case FakeMode.EchoError:
                await WriteCompatibleErrorAsync(context, 400, $"Cannot read this input: {text} with key {_apiKey}");
                return;
            case FakeMode.Status:
                await WriteCompatibleErrorAsync(context, Behavior.Status, $"The fake answers with this status while it reads: {text} with key {_apiKey}");
                return;
            case FakeMode.Redirect:
                context.Response.StatusCode = 307;
                context.Response.Headers.Location = $"{Address}/stolen";
                return;
            case FakeMode.Hold when Behavior.HoldOnly is null || text.Contains(Behavior.HoldOnly, StringComparison.Ordinal):
                await Task.Delay(Behavior.DelayMs, context.RequestAborted);
                break;
        }

        var finishReason = Behavior.Mode switch
        {
            FakeMode.Truncate => "length",
            FakeMode.Refusal => "content_filter",
            _ => "stop",
        };
        if (document.RootElement.TryGetProperty("stream", out var stream) && stream.GetBoolean())
        {
            await WriteCompatibleStreamAsync(context, ReplyFor(text), finishReason, text);
            return;
        }

        await context.Response.WriteAsJsonAsync(new
        {
            id = "chatcmpl-fake",
            @object = "chat.completion",
            created = 1_700_000_000,
            model = ModelId,
            choices = new[]
            {
                new { index = 0, message = new { role = "assistant", content = ReplyFor(text) }, finish_reason = finishReason },
            },
        });
    }

    private async Task WriteCompatibleStreamAsync(HttpContext context, string reply, string finishReason, string requestText)
    {
        context.Response.ContentType = "text/event-stream";
        await WriteCompatibleChunkAsync(context, new { role = "assistant", content = (string?)null, refusal = (string?)null }, null);
        await context.Response.WriteAsync(": keep-alive\n\n");
        await context.Response.Body.FlushAsync();
        switch (Behavior.Mode)
        {
            case FakeMode.StallMidStream:
                await Task.Delay(Timeout.Infinite, context.RequestAborted);
                return;
            case FakeMode.MidStreamError:
                await context.Response.WriteAsync($"data: {JsonSerializer.Serialize(new { error = new { message = $"Stopped at: {requestText} with key {_apiKey}", type = "server_error" } })}\n\n");
                return;
            case FakeMode.KeepAliveForever:
                while (true)
                {
                    await Task.Delay(Behavior.DelayMs, context.RequestAborted);
                    await context.Response.WriteAsync(": keep-alive\n\n");
                    await context.Response.Body.FlushAsync();
                    await WriteCompatibleChunkAsync(context, new { content = " " }, null);
                    await WriteCompatibleChunkAsync(context, new { reasoning = new { text = "" }, reasoning_content = new object[] { "" } }, null);
                }
            case FakeMode.Thinking:
                for (var thought = 0; thought < ThoughtsOfAModel; thought++)
                {
                    await Task.Delay(Behavior.DelayMs, context.RequestAborted);
                    await WriteCompatibleChunkAsync(context, new { reasoning_content = "hmm" }, null);
                }

                break;
        }

        foreach (var part in PartsOf(reply))
        {
            await WriteCompatibleChunkAsync(context, new { content = part }, null);
            if (Behavior.Mode == FakeMode.SlowWords)
                await Task.Delay(Behavior.DelayMs, context.RequestAborted);
        }

        if (Behavior.Mode == FakeMode.CutStream)
            return;

        await WriteCompatibleChunkAsync(context, new { }, finishReason);
        await context.Response.WriteAsync($"data: {JsonSerializer.Serialize(new { id = "chatcmpl-fake", @object = "chat.completion.chunk", choices = Array.Empty<object>(), usage = new { prompt_tokens = 1, completion_tokens = 1, total_tokens = 2 } })}\n\ndata: [DONE]\n\n");
    }

    private static async Task WriteCompatibleChunkAsync(HttpContext context, object delta, string? finishReason)
    {
        var chunk = new { id = "chatcmpl-fake", @object = "chat.completion.chunk", created = 1_700_000_000, model = ModelId, choices = new[] { new { index = 0, delta, finish_reason = finishReason } } };
        await context.Response.WriteAsync($"data: {JsonSerializer.Serialize(chunk)}\n\n");
        await context.Response.Body.FlushAsync();
    }

    private async Task<bool> RefusedCompatibleAsync(HttpContext context)
    {
        if (context.Request.Headers.Authorization != $"Bearer {_apiKey}")
        {
            await WriteCompatibleErrorAsync(context, 401, "Incorrect API key provided.");
            return true;
        }

        return false;
    }

    private static Task WriteCompatibleErrorAsync(HttpContext context, int status, string message)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(new { error = new { message, type = "invalid_request_error", code = (string?)null } });
    }

    private string ReplyFor(string requestText)
    {
        if (Behavior.Mode == FakeMode.BadJson)
            return "This is no object.";

        if (Behavior.Reply is { } reply)
            return reply;

        var words = requestText.Replace("<raw>", string.Empty, StringComparison.Ordinal).Replace("</raw>", string.Empty, StringComparison.Ordinal).Trim();
        return JsonSerializer.Serialize(new
        {
            cleanedMarkdown = $"## Notes\n\n{words} (cleaned)",
            synopsis = "I wrote some words.",
            topicSuggestions = DefaultTopics,
            peopleProposal = Array.Empty<string>(),
            moodSuggestions = DefaultMoods,
        });
    }

    private static string UserTextOfAnthropic(JsonElement root)
    {
        var content = root.GetProperty("messages")[0].GetProperty("content");
        return content.ValueKind == JsonValueKind.String ? content.GetString()! : content[0].GetProperty("text").GetString()!;
    }

    private static string UserTextOfCompatible(JsonElement root)
        => root.GetProperty("messages").EnumerateArray().First(m => m.GetProperty("role").GetString() == "user").GetProperty("content").GetString()!;

    private static async Task<string> ReadBodyAsync(HttpContext context)
    {
        using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
        return await reader.ReadToEndAsync(context.RequestAborted);
    }
}
