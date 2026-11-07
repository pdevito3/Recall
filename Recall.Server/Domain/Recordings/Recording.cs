namespace Recall.Server.Domain.Recordings;

using Exceptions;

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

    public static IReadOnlyList<int> MissingSlices(IEnumerable<int> stored, int sliceCount)
    {
        var present = stored.ToHashSet();
        return Enumerable.Range(0, sliceCount).Where(index => !present.Contains(index)).ToList();
    }

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
        if (WaitsForTranscription)
            return;

        if (Status != RecordingStatus.Failed)
            throw new ConflictException(StatusField, "Only a Recording that failed or that waits for its text can run again.");

        Status = RecordingStatus.Received;
        Problem = RecordingProblem.None;
        AttemptsWithoutProgress = 0;
        if (!HasText)
            TranscribedSamples = 0;
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

public enum TranscriptionEngineKind
{
    Local,
}

public enum RecordingProblem
{
    None,
    WaitingForModel,
    AudioUnreadable,
    AudioMissing,
    DiskFull,
    DiskRefused,
    OutOfMemory,
    EngineKilled,
    EngineCrashed,
    EngineStalled,
    EngineNotInstalled,
    TooManyAttempts,
    TooLong,
    TextTooLong,
    NoWordsFound,
}
