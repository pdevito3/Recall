namespace Recall.Server.Domain.ModelProviders.Dtos;

using Recordings;
using Resources;

/// <summary>Where the audio of the tenant becomes text, and why.</summary>
public sealed record TranscriptionEngineDto
{
    [EnumNames<TranscriptionEngineKind>]
    public string Engine { get; init; } = default!;

    [EnumNames<TranscriptionEngineReason>]
    public string Reason { get; init; } = default!;

    /// <summary>What it means for the audio, for a person.</summary>
    public string Text { get; init; } = default!;

    public string? ModelProviderId { get; init; }
    public string? ModelProviderName { get; init; }
    public string? Model { get; init; }
}

public sealed record TranscriptionEngineForUpdateDto
{
    /// <summary>The provider that gets the audio. Null sends no audio out, and the local engine makes the text.</summary>
    public string? ModelProviderId { get; init; }

    /// <summary>The model that turns audio into text, such as <c>gpt-transcribe</c> or <c>whisper-large-v3-turbo</c>.</summary>
    [SchemaMaxLength(typeof(ModelProvider), nameof(ModelProvider.ModelMaxLength))]
    public string? Model { get; init; }
}

public enum TranscriptionEngineReason
{
    ProviderChosen,
    NoProviderTakesAudio,
    NoProviderChosen,
}
