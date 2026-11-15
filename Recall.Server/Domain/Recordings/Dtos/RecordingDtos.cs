namespace Recall.Server.Domain.Recordings.Dtos;

using Resources;
using Transcription;

public sealed record RecordingForCreationDto
{
    /// <summary>The ID that the device made for this Recording. A second create with this ID returns the same Recording.</summary>
    [RequiredField]
    [SchemaMaxLength(typeof(Recording), nameof(Recording.ClientRecordingIdMaxLength))]
    public string ClientRecordingId { get; init; } = default!;

    [RequiredField]
    [EnumNames<RecordingFormat>]
    public string Format { get; init; } = default!;
}

public sealed record RecordingFinishDto
{
    public int SliceCount { get; init; }
    public long ByteLength { get; init; }

    /// <summary>
    /// Each tap of New paragraph, in ms from the start of the audio. Optional, so a client with no button sends none. The
    /// server never refuses them: it sorts them, drops repeats and taps outside 0 to 4 hours, and keeps the first 1,000. The
    /// first finish stores them, and a repeat finish keeps them.
    /// </summary>
    public IReadOnlyList<int>? ParagraphTapsMs { get; init; }
}

public sealed record RecordingSliceDto
{
    public int Index { get; init; }
    public long ByteLength { get; init; }
    public string Sha256 { get; init; } = default!;
    public int? HighestStoredSlice { get; init; }
}

public sealed record RecordingDto
{
    public string Id { get; init; } = default!;
    public string SessionId { get; init; } = default!;
    public string ClientRecordingId { get; init; } = default!;
    [EnumNames<RecordingFormat>]
    public string Format { get; init; } = default!;
    [EnumNames<TranscriptionEngineKind>]
    public string Engine { get; init; } = default!;
    [EnumNames<RecordingStatus>]
    public string Status { get; init; } = default!;
    [EnumNames<RecordingProblem>]
    public string Problem { get; init; } = default!;

    /// <summary>What happened and what to do, for a person. Null when nothing is wrong.</summary>
    public string? ProblemText { get; init; }

    public int? HighestStoredSlice { get; init; }

    /// <summary>The indexes that are stored, so that a client can send the ones that a broken upload lost.</summary>
    public IReadOnlyList<int> StoredSlices { get; init; } = [];

    public long ByteLength { get; init; }
    public long? DurationMs { get; init; }
    public long TranscribedMs { get; init; }

    /// <summary>The name for the file route. Null while the upload runs and after the audio is gone.</summary>
    public string? AudioFileName { get; init; }

    public DateTimeOffset? AudioKeptUntil { get; init; }
    public uint Version { get; init; }
}

/// <summary>What a person needs to know about the work behind all audio, whatever the Recording.</summary>
public sealed record TranscriptionStatusDto
{
    public bool JobsStopped { get; init; }

    /// <summary>What it means and what to do, when the jobs stopped. It names no fault.</summary>
    public string? JobsStoppedText { get; init; }

    [EnumNames<ModelStatus>]
    public string Model { get; init; } = default!;

    public string? ModelText { get; init; }
}
