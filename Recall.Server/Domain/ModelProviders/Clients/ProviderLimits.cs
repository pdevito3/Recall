namespace Recall.Server.Domain.ModelProviders.Clients;

/// <summary>
/// The limits of each call to a model provider. <c>docs/backend/model-providers.md</c> states why each one has its value.
/// A test can shorten one.
/// </summary>
public sealed record ProviderLimits
{
    /// <summary>A wait for one read of the answer, also for its headers. A model that works sends a byte in this time.</summary>
    public TimeSpan Idle { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>A wait for the next word or the next thought of the model. A ping is a byte and no progress, and a run with only pings holds a slot of a queue.</summary>
    public TimeSpan NoWords { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>One call for words with its whole stream, and each retry of the run together.</summary>
    public TimeSpan Call { get; init; } = TimeSpan.FromMinutes(40);

    /// <summary>A call for the list of models. A person waits for it, and no model runs.</summary>
    public TimeSpan Listing { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan Connect { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>A wait for one read of the answer to a part of audio, also for its headers. A provider sends no byte until it heard the whole part, and a speech server on a CPU can take minutes for one.</summary>
    public TimeSpan AudioIdle { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>One call with one part of audio: the upload, the wait, and the answer.</summary>
    public TimeSpan AudioCall { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>The limits of the HTTP client that sends audio. Only the wait for a read differs.</summary>
    public ProviderLimits ForAudio() => this with { Idle = AudioIdle };

    /// <summary>An answer that is not a stream, such as a list of models or an error.</summary>
    public long BodyBytes { get; init; } = 16 * 1024 * 1024;

    /// <summary>A stream of the longest answer: 250 000 tokens with an envelope of up to 250 bytes for each.</summary>
    public long StreamBytes { get; init; } = 64 * 1024 * 1024;

    /// <summary>One line of a stream. The text of one event goes to memory as a whole.</summary>
    public long StreamLineBytes { get; init; } = 4 * 1024 * 1024;

    /// <summary>The longest answer that can give a Draft: the limit of a Draft with an escape for each character.</summary>
    public int AnswerCharacters { get; init; } = 2 * Sessions.Session.DraftMaxLength;

    public int ModelPages { get; init; } = 10;

    public int ModelsInAPage { get; init; } = 1_000;

    public int Models { get; init; } = 2_000;
}
