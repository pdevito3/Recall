namespace Recall.Server.Domain.Recordings.Dtos;

using Features;
using Transcription;

public static class RecordingViews
{
    public static RecordingDto ToDto(this Recording recording, IReadOnlyList<int> storedSlices, ModelStatus model, bool jobsStopped) => new()
    {
        Id = recording.Id,
        SessionId = recording.SessionId,
        ClientRecordingId = recording.ClientRecordingId,
        Format = recording.Format.ToString(),
        Engine = recording.Engine.ToString(),
        Status = recording.Status.ToString(),
        Problem = recording.Problem.ToString(),
        ProblemText = recording.WaitsForTranscription && jobsStopped
            ? GetTranscriptionStatus.JobsStoppedText
            : recording is { Status: RecordingStatus.Failed, Problem: RecordingProblem.ProviderUnavailable }
                ? ProviderGaveUpText
                : ProblemTextOf(recording.Problem, model),
        HighestStoredSlice = recording.HighestStoredSlice,
        StoredSlices = storedSlices,
        ByteLength = recording.ByteLength,
        DurationMs = recording.DurationMs,
        TranscribedMs = recording.TranscribedSamples * 1000 / Recording.SamplesPerSecond,
        AudioFileName = recording.Status == RecordingStatus.Receiving || recording.AudioDeletedAt is not null ? null : recording.AudioFileName,
        AudioKeptUntil = recording.AudioKeptUntil,
        Version = recording.Version,
    };

    /// <summary>The tries of the job ended, so no new try comes unless a person asks for one.</summary>
    public const string ProviderGaveUpText = "The audio is stored, but the provider that makes the text did not answer on any try. "
        + "Try again later. If it keeps failing, ask an Admin to check the provider.";

    public static string? ProblemTextOf(RecordingProblem problem, ModelStatus model) => problem switch
    {
        RecordingProblem.None => null,
        RecordingProblem.WaitingForModel => ModelTextOf(model) ?? "The audio is stored and waits for its turn.",
        RecordingProblem.AudioUnreadable => "The audio is stored, but the server cannot read it. Download it to keep a copy, then record again.",
        RecordingProblem.AudioMissing => "The server cannot find the audio of this recording. Tell the Owner of this server.",
        RecordingProblem.DiskFull => "The audio is stored, but the disk of the server is full, so the server cannot make the text. Ask the Owner to free some space, then try again.",
        RecordingProblem.DiskRefused => "The audio is stored, but the disk of the server refused the work, so the server cannot make the text. Ask the Owner to check the disk and its permissions, then try again.",
        RecordingProblem.OutOfMemory => "The audio is stored, but the speech engine was stopped, and the server ran out of memory during the same try. Ask the Owner to check the memory of the server, then try again.",
        RecordingProblem.EngineKilled => "The audio is stored, but the speech engine was stopped before it finished. Try again. If it stops again, tell the Owner of this server.",
        RecordingProblem.EngineCrashed => "The audio is stored, but the speech engine stopped with an error. Try again. If it stops again, tell the Owner of this server.",
        RecordingProblem.EngineStalled => "The audio is stored, but the speech engine stopped making progress. Try again when the server has less work.",
        RecordingProblem.EngineNotInstalled => "The audio is stored, but this server has no speech engine. The Owner must install the release image.",
        RecordingProblem.TooManyAttempts => "The audio is stored, but the server restarted during each try to make the text. Ask the Owner to check the server, then try again.",
        RecordingProblem.TooLong => "The audio is stored, but it is longer than the 4 hours that the server can turn into text. Download it to keep a copy.",
        RecordingProblem.TextTooLong => "The audio is stored, but its text does not fit in this Session. The text so far is saved. Download the audio to keep a copy.",
        RecordingProblem.NoWordsFound => "The audio is stored, but the server heard no words in it. Listen to it to check, or try again.",
        RecordingProblem.ProviderUnavailable => "The audio is stored, but the provider that makes the text did not answer. The server tries again on its own. If it keeps failing, ask an Admin to check the provider.",
        RecordingProblem.ProviderRefused => "The audio is stored, but the provider that makes the text refused it. Ask an Admin to check the key, the model, and the limits of the provider, then try again.",
        RecordingProblem.ProviderBadAnswer => "The audio is stored, but the provider that makes the text gave an answer that the server cannot read. Ask an Admin to check the model of the provider, then try again.",
        RecordingProblem.ProviderKeyUnreadable => "The audio is stored, but this server cannot read the key of the provider that makes the text. Ask an Admin to enter the key again, then try again.",
        RecordingProblem.NoAudioReceived => "No audio of this recording reached the server within a day, so the server ended it.",
        _ => null,
    };

    public static string? ModelTextOf(ModelStatus model) => model switch
    {
        ModelStatus.Missing => "The audio is stored. The server must get the speech model first, and then it makes the text.",
        ModelStatus.Downloading => "The audio is stored. The server is downloading the speech model, and then it makes the text.",
        ModelStatus.DownloadFailed => "The audio is stored. The server could not download the speech model, so it makes no text yet. It tries again on its own. Ask the Owner to check the network and the free disk space of the server.",
        ModelStatus.HashRejected => "The audio is stored. The speech model that the server downloaded did not pass its hash check, so the server did not use it. It tries again on its own.",
        _ => null,
    };
}
