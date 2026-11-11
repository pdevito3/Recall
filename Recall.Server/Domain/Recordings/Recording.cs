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

    /// <summary>An upload that gets no new slice for this long ends with the slices that came. See <see cref="EndQuietUpload"/>.</summary>
    public static readonly TimeSpan QuietUploadEndsAfter = TimeSpan.FromHours(24);

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

    /// <summary>
    /// Set while the upload stays as the sweep ended it (REC-23): the highest slice in the audio file, or -1 with no slice.
    /// A late slice above it opens the upload again (REC-24), and a finish clears it.
    /// </summary>
    public int? QuietEndThroughSlice { get; private set; }

    public long ByteLength { get; private set; }

    public long? DurationMs { get; private set; }

    public long TranscribedSamples { get; private set; }

    public bool HasText { get; private set; }

    public int AttemptsWithoutProgress { get; private set; }

    public DateTimeOffset? AudioKeptUntil { get; private set; }

    public DateTimeOffset? AudioDeletedAt { get; private set; }

    /// <summary>The file of a stored slice was gone when the audio was joined, so the audio has a gap, and its words have one too.</summary>
    public bool HasLostAudio { get; private set; }

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
        if (index <= QuietEndThroughSlice)
            throw new ConflictException(SliceField, "The server ended this upload with a later slice, so this slice cannot join the audio.");
        if (Status != RecordingStatus.Receiving)
            OpenForLatePart();

        if (ByteLength + byteLength > MaxBytes)
            throw new ConflictException(ByteLengthField, "This recording is too large to store. The slices that are stored stay.");

        ByteLength += byteLength;
        HighestStoredSlice = Math.Max(HighestStoredSlice ?? -1, index);
    }

    /// <summary>
    /// REC-24: a client that kept the rest of the audio, and comes back after the sweep ended the upload, sends it here. The
    /// upload opens again, and the finish adds the late part to the audio, so its words come after the words that the entry has.
    /// A slice inside the audio that the sweep joined cannot join it, because the words after it are already made.
    /// </summary>
    private void OpenForLatePart()
    {
        if (QuietEndThroughSlice is null)
            throw new ConflictException(StatusField, "This recording is finished, so it takes no new slice.");
        if (AudioDeletedAt is not null)
            throw new ConflictException(StatusField, "The server removed the audio of this recording, so a late part cannot join it.");

        Status = RecordingStatus.Receiving;
        Problem = RecordingProblem.None;
        AttemptsWithoutProgress = 0;
    }

    public static IReadOnlyList<int> MissingSlices(IEnumerable<int> stored, int sliceCount)
    {
        var present = stored.ToHashSet();
        return Enumerable.Range(0, sliceCount).Where(index => !present.Contains(index)).ToList();
    }

    /// <summary>The engine is chosen here and at a retry of a failure only, so two engines never work on one Recording at a time.</summary>
    public void Finish(IReadOnlyCollection<int> stored, int sliceCount, long byteLength, TranscriptionEngineKind engine)
    {
        if (Status != RecordingStatus.Receiving)
            throw new ConflictException(StatusField, "This recording is already finished.");

        ValidationException.Must(sliceCount is > 0 and <= MaxSlices, SliceCountField, $"A Recording has from 1 to {MaxSlices} slices.");
        // A gap that the sweep joined stays a gap (REC-24), so only the slices after the joined audio must all be here.
        if (MissingSlices(stored, sliceCount).Any(index => index > QuietEndThroughSlice.GetValueOrDefault(-1)))
            throw new ConflictException(SliceCountField, "Some slices are missing. Send them, then finish again.");
        if (stored.Any(index => index >= sliceCount))
            throw new ConflictException(SliceCountField, "More slices are stored than this finish names.");
        if (byteLength != ByteLength)
            throw new ConflictException(ByteLengthField, "The stored slices do not have the byte length that this finish names.");

        Status = RecordingStatus.Received;
        Engine = engine;
        QuietEndThroughSlice = null;
    }

    /// <summary>
    /// A client that never sends the rest or the finish must not leave a Recording that waits for good. The slices that came
    /// make the audio, in the order of their index, also with a gap, so the text of each part that the server has still comes.
    /// With no slice there is no audio, so the Recording fails with a reason, and a retry cannot start it. The client can still
    /// send the rest later (REC-24).
    /// </summary>
    public void EndQuietUpload(int storedSliceCount, TranscriptionEngineKind engine)
    {
        if (Status != RecordingStatus.Receiving)
            throw new ConflictException(StatusField, "This recording is already finished.");

        QuietEndThroughSlice = HighestStoredSlice ?? -1;
        if (storedSliceCount == 0)
        {
            Status = RecordingStatus.Failed;
            Problem = RecordingProblem.NoAudioReceived;
            return;
        }

        Status = RecordingStatus.Received;
        Engine = engine;
    }

    /// <summary>A provider that did not answer on each try is the cause that a person needs, and not the count of the tries.</summary>
    public bool BeginAttempt()
    {
        RequireWaitingForTranscription();
        var causeOfLastTry = Problem;
        Status = RecordingStatus.Transcribing;
        Problem = RecordingProblem.None;
        AttemptsWithoutProgress++;
        if (AttemptsWithoutProgress <= MaxAttemptsWithoutProgress)
            return true;

        Fail(causeOfLastTry == RecordingProblem.ProviderUnavailable ? causeOfLastTry : RecordingProblem.TooManyAttempts);
        return false;
    }

    /// <summary>The provider did not answer, and the job tries again later.</summary>
    public void WaitForProvider()
    {
        RequireWaitingForTranscription();
        Status = RecordingStatus.Received;
        Problem = RecordingProblem.ProviderUnavailable;
    }

    /// <summary>No provider takes the audio of the tenant any more, so the audio stays on the server, and the local engine makes the text.</summary>
    public void HandToLocalEngine()
    {
        RequireWaitingForTranscription();
        Engine = TranscriptionEngineKind.Local;
        Status = RecordingStatus.Received;
        Problem = RecordingProblem.None;
        AttemptsWithoutProgress = 0;
    }

    public void WaitForModel()
    {
        RequireWaitingForTranscription();
        Status = RecordingStatus.Received;
        Problem = RecordingProblem.WaitingForModel;
        AttemptsWithoutProgress = 0;
    }

    /// <summary>
    /// A chunk that the saved text already holds joins as nothing. Any other chunk must start where the text ends, so a run with
    /// other cuts, or a second run, repeats no word and leaves no gap.
    /// </summary>
    public bool Joins(long fromSample, long endSample) => endSample <= TranscribedSamples || fromSample == TranscribedSamples;

    /// <summary>
    /// A run reads the audio as it was at its begin. A late part (REC-24) makes the audio longer, so a run that began before it
    /// must not end the Recording: the run begins again and goes on from the saved text.
    /// </summary>
    public bool AudioGrewSince(int? highestSliceAtBegin) => HighestStoredSlice != highestSliceAtBegin;

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

    /// <summary>A Recording that still waits keeps its engine, because its job can still run.</summary>
    public void Retry(TranscriptionEngineKind engine)
    {
        if (WaitsForTranscription)
            return;

        if (Status != RecordingStatus.Failed)
            throw new ConflictException(StatusField, "Only a recording that failed or that waits for its text can run again.");
        if (Problem == RecordingProblem.NoAudioReceived)
            throw new ConflictException(StatusField, "No audio of this recording reached the server, so there is no audio to make text of.");

        Engine = engine;
        Status = RecordingStatus.Received;
        Problem = RecordingProblem.None;
        AttemptsWithoutProgress = 0;
        if (!HasText)
            TranscribedSamples = 0;
    }

    public bool CanDeleteAudio(DateTimeOffset now)
        => Status == RecordingStatus.Transcribed && AudioDeletedAt is null && AudioKeptUntil is { } until && until <= now;

    public void MarkAudioDeleted(DateTimeOffset now) => AudioDeletedAt = now;

    public void MarkAudioLost() => HasLostAudio = true;

    private void RequireWaitingForTranscription()
    {
        if (!WaitsForTranscription)
            throw new ConflictException(StatusField, "This recording is not waiting for its text.");
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
    /// <summary>Parakeet in a subprocess on the server. The audio stays on the server.</summary>
    Local,

    /// <summary>The model provider that an Admin chose for audio. The audio goes to that provider.</summary>
    Cloud,
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
    ProviderUnavailable,
    ProviderRefused,
    ProviderBadAnswer,
    ProviderKeyUnreadable,
    NoAudioReceived,
}
