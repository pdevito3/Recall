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

    /// <summary>A 200 with a cut answer: <c>max_tokens</c> or <c>length</c>.</summary>
    Truncate,

    /// <summary>A 200 with a refusal: <c>refusal</c> or <c>content_filter</c>.</summary>
    Refusal,

    /// <summary>A 200 with words that are no JSON object.</summary>
    BadJson,

    /// <summary>A redirect to <c>/stolen</c>. A client that follows it sends the key there.</summary>
    Redirect,

    /// <summary>An Anthropic stream with an <c>error</c> event after the 200. The event holds the words of the request.</summary>
    MidStreamError,
}

public sealed class FakeBehavior
{
    public FakeMode Mode { get; set; } = FakeMode.Normal;

    public int Status { get; set; } = 500;

    public int DelayMs { get; set; }

    /// <summary>The words of the answer in place of the object that the fake makes from the request.</summary>
    public string? Reply { get; set; }
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

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly WebApplication _app;
    private readonly string _apiKey;
    private int _completionCalls;
    private int _modelListCalls;
    private int _stolenCalls;

    private FakeModelProvider(WebApplication app, string apiKey)
    {
        _app = app;
        _apiKey = apiKey;
    }

    public FakeBehavior Behavior { get; private set; } = new();

    public int CompletionCalls => Volatile.Read(ref _completionCalls);

    public int ModelListCalls => Volatile.Read(ref _modelListCalls);

    public int StolenCalls => Volatile.Read(ref _stolenCalls);

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
        _app.MapGet("/v1/models", AnthropicModels);
        _app.MapGet("/v1/models/{id}", AnthropicModel);
        _app.MapPost("/v1/messages", AnthropicMessage);
        _app.MapGet("/compat/v1/models", CompatibleModels);
        _app.MapPost("/compat/v1/chat/completions", CompatibleCompletion);
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
        _app.MapGet("/control", (HttpContext context) => context.Response.WriteAsJsonAsync(new { completionCalls = CompletionCalls, modelListCalls = ModelListCalls, stolenCalls = StolenCalls }));
    }

    private async Task AnthropicModels(HttpContext context)
    {
        Interlocked.Increment(ref _modelListCalls);
        if (await RefusedAnthropicAsync(context))
            return;

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
            case FakeMode.Hold:
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
        await WriteEventAsync(context, "content_block_start", new { type = "content_block_start", index = 0, content_block = new { type = "text", text = string.Empty } });
        if (Behavior.Mode == FakeMode.MidStreamError)
        {
            await WriteEventAsync(context, "error", new { type = "error", error = new { type = "overloaded_error", message = $"Stopped at: {requestText} with key {_apiKey}" } });
            return;
        }

        await WriteEventAsync(context, "content_block_delta", new { type = "content_block_delta", index = 0, delta = new { type = "text_delta", text = reply } });
        await WriteEventAsync(context, "content_block_stop", new { type = "content_block_stop", index = 0 });
        await WriteEventAsync(context, "message_delta", new
        {
            type = "message_delta",
            delta = new { stop_reason = stopReason, stop_sequence = (string?)null },
            usage = new { output_tokens = 5 },
        });
        await WriteEventAsync(context, "message_stop", new { type = "message_stop" });
    }

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
            case FakeMode.Hold:
                await Task.Delay(Behavior.DelayMs, context.RequestAborted);
                break;
        }

        var finishReason = Behavior.Mode switch
        {
            FakeMode.Truncate => "length",
            FakeMode.Refusal => "content_filter",
            _ => "stop",
        };
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
