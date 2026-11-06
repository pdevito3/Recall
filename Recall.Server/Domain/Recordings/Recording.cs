namespace Recall.Server.Domain.Recordings;

using Exceptions;

/// <summary>
/// Audio of one Session. Its words reach Raw through the status machine
/// <c>Receiving -finish-> Received -attempt-> Transcribing -complete-> Transcribed</c>. A Recording that is
/// <c>Received</c> or <c>Transcribing</c> can go to <c>Failed</c>, and a person can send <c>Failed</c> back to
/// <c>Received</c>. Only <c>Transcribed</c> audio ever goes away by itself.
/// </summary>
public class Recording : BaseEntity, ITenantable, IJournalOwned
{
    public const string IdPrefix = "rec";
    public const int ClientRecordingIdMaxLength = 100;
    public const int MaxSliceBytes = 8 * 1024 * 1024;
    public const long MaxBytes = 1024L * 1024 * 1024;
    public const int MaxSlices = 20_000;
    public const int MaxAttemptsWithoutProgress = 5;
    public const int SamplesPerSecond = 16_000;
    public static readonly TimeSpan AudioKeptAfterText = TimeSpan.FromDays(7);
    public static readonly TimeSpan MaxDuration = TimeSpan.FromHours(4);

    private const string ClientRecordingIdField = "clientRecordingId";
    private const string SliceField = "index";
    private const string SliceCountField = "sliceCount";
    private const string ByteLengthField = "byteLength";
    private const string StatusField = "status";

    [NotSensitive] public string TenantId { get; private set; } = default!;

    [NotSensitive] public string UserId { get; private set; } = default!;

    [NotSensitive] public string SessionId { get; private set; } = default!;

    [NotSensitive] public string ClientRecordingId { get; private set; } = default!;

    public RecordingFormat Format { get; private set; }

    public TranscriptionEngineKind Engine { get; private set; }

    public RecordingStatus Status { get; private set; }

    public RecordingProblem Problem { get; private set; }

    public int? HighestStoredSlice { get; private set; }

    public long ByteLength { get; private set; }

    public long? DurationMs { get; private set; }

    public long TranscribedSamples { get; private set; }

    public bool HasText { get; private set; }

    public int AttemptsWithoutProgress { get; private set; }

    public DateTimeOffset? AudioKeptUntil { get; private set; }

    public DateTimeOffset? AudioDeletedAt { get; private set; }

    public string AudioFileName => Format == RecordingFormat.Webm ? "audio.webm" : "audio.mp4";

    public string AudioContentType => Format == RecordingFormat.Webm ? "audio/webm" : "audio/mp4";

    /// <summary>True while a job has work on this Recording, so a start of the server must queue that work.</summary>
    public bool WaitsForTranscription => Status is RecordingStatus.Received or RecordingStatus.Transcribing;

    public static Recording Create(string tenantId, string userId, string sessionId, string clientRecordingId, RecordingFormat format)
    {
        ValidationException.ThrowWhenNullOrWhitespace(clientRecordingId, ClientRecordingIdField, "Please provide a client recording ID.");
        ValidationException.ThrowWhenLongerThan(clientRecordingId, ClientRecordingIdMaxLength, ClientRecordingIdField,
            $"The client recording ID must not have more than {ClientRecordingIdMaxLength} characters.");
        ValidationException.ThrowWhenHasCharacterNotStorable(clientRecordingId, ClientRecordingIdField,
            "The client recording ID has a character that is not allowed.");

        return new Recording
        {
            TenantId = tenantId,
            UserId = userId,
            SessionId = sessionId,
            ClientRecordingId = clientRecordingId,
            Format = format,
            Engine = TranscriptionEngineKind.Local,
            Status = RecordingStatus.Receiving,
        };
    }

    public static void ValidateSliceIndex(int index)
        => ValidationException.Must(index is >= 0 and < MaxSlices, SliceField, $"A slice index must be from 0 to {MaxSlices - 1}.");

    public void StoreSlice(int index, long byteLength)
    {
        ValidateSliceIndex(index);
        if (Status != RecordingStatus.Receiving)
            throw new ConflictException(StatusField, "This Recording is finished, so it takes no new slice.");

        if (ByteLength + byteLength > MaxBytes)
            throw new ConflictException(ByteLengthField, "This Recording is too large to store. The slices that are stored stay.");

        ByteLength += byteLength;
        HighestStoredSlice = Math.Max(HighestStoredSlice ?? -1, index);
    }

    /// <summary>The slice indexes below <paramref name="sliceCount"/> that are not stored.</summary>
    public static IReadOnlyList<int> MissingSlices(IEnumerable<int> stored, int sliceCount)
    {
        var present = stored.ToHashSet();
        return Enumerable.Range(0, sliceCount).Where(index => !present.Contains(index)).ToList();
    }

    /// <summary>Ends the upload. It throws when the slices do not add up, so a person can send what is missing.</summary>
    public void Finish(IReadOnlyCollection<int> stored, int sliceCount, long byteLength)
    {
        if (Status != RecordingStatus.Receiving)
            throw new ConflictException(StatusField, "This Recording is already finished.");

        ValidationException.Must(sliceCount is > 0 and <= MaxSlices, SliceCountField, $"A Recording has from 1 to {MaxSlices} slices.");
        if (MissingSlices(stored, sliceCount).Count > 0)
            throw new ConflictException(SliceCountField, "Some slices are missing. Send them, then finish again.");
        if (stored.Count != sliceCount)
            throw new ConflictException(SliceCountField, "More slices are stored than this finish names.");
        if (byteLength != ByteLength)
            throw new ConflictException(ByteLengthField, "The stored slices do not have the byte length that this finish names.");

        Status = RecordingStatus.Received;
    }

    /// <summary>Starts one attempt of a job. It returns false when the attempts ran out, so the status is now Failed.</summary>
    public bool BeginAttempt()
    {
        RequireWaitingForTranscription();
        Status = RecordingStatus.Transcribing;
        Problem = RecordingProblem.None;
        AttemptsWithoutProgress++;
        if (AttemptsWithoutProgress <= MaxAttemptsWithoutProgress)
            return true;

        Fail(RecordingProblem.TooManyAttempts);
        return false;
    }

    public void WaitForModel()
    {
        RequireWaitingForTranscription();
        Status = RecordingStatus.Received;
        Problem = RecordingProblem.WaitingForModel;
        AttemptsWithoutProgress = 0;
    }

    /// <summary>
    /// Moves the progress pointer past one chunk. The caller saves it in one transaction with the words of the chunk, so a
    /// later attempt starts after these words. False when the pointer is already past the chunk.
    /// </summary>
    public bool AdvanceTo(long endSample, long totalSamples, bool hasWords)
    {
        DurationMs = totalSamples * 1000 / SamplesPerSecond;
        if (endSample <= TranscribedSamples)
            return false;

        TranscribedSamples = endSample;
        AttemptsWithoutProgress = 0;
        HasText |= hasWords;
        return true;
    }

    /// <summary>Ends the work. With no words at all the audio is all that the person has, so it stays with a reason.</summary>
    public void Complete(DateTimeOffset now)
    {
        RequireWaitingForTranscription();
        if (!HasText)
        {
            Fail(RecordingProblem.NoWordsFound);
            return;
        }

        Status = RecordingStatus.Transcribed;
        Problem = RecordingProblem.None;
        AudioKeptUntil = now + AudioKeptAfterText;
    }

    public void Fail(RecordingProblem problem)
    {
        RequireWaitingForTranscription();
        Status = RecordingStatus.Failed;
        Problem = problem;
    }

    public void Retry()
    {
        if (Status != RecordingStatus.Failed)
            throw new ConflictException(StatusField, "Only a Recording that failed can run again.");

        Status = RecordingStatus.Received;
        Problem = RecordingProblem.None;
        AttemptsWithoutProgress = 0;
    }

    public bool CanDeleteAudio(DateTimeOffset now)
        => Status == RecordingStatus.Transcribed && AudioDeletedAt is null && AudioKeptUntil is { } until && until <= now;

    public void MarkAudioDeleted(DateTimeOffset now) => AudioDeletedAt = now;

    private void RequireWaitingForTranscription()
    {
        if (!WaitsForTranscription)
            throw new ConflictException(StatusField, "This Recording is not waiting for its text.");
    }

    protected Recording() : base(IdPrefix) { }
}

public enum RecordingStatus
{
    Receiving,
    Received,
    Transcribing,
    Transcribed,
    Failed,
}

public enum RecordingFormat
{
    Webm,
    Mp4,
}

/// <summary>Which engine makes the text. <see cref="Transcription.ITranscriptionEngine"/> is the seam that a cloud engine implements.</summary>
public enum TranscriptionEngineKind
{
    Local,
}

/// <summary>Why a Recording has no text yet. It is a closed list, so it can hold no text of a person or of an exception.</summary>
public enum RecordingProblem
{
    None,
    WaitingForModel,
    AudioUnreadable,
    AudioMissing,
    OutOfMemory,
    EngineCrashed,
    EngineStalled,
    EngineNotInstalled,
    TooManyAttempts,
    TooLong,
    TextTooLong,
    NoWordsFound,
}
