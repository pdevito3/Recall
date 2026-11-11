namespace Recall.Server.Domain.Recordings.Transcription;

using System.Globalization;
using Microsoft.Extensions.Options;

/// <summary>
/// The stored audio decoded once to 16 kHz mono PCM in a WAV file, with the ffmpeg of the image. Each engine reads this file, so
/// each engine counts the same samples, and a pointer that one attempt saved means the same place for the next attempt.
/// </summary>
public sealed class WorkAudio(IOptions<TranscriptionOptions> options)
{
    private const int BytesPerSample = 2;

    // The words of ffmpeg 7.1.3 on its error stream, under the C locale of the clean environment of Start. The exit code
    // is not stable across ffmpeg versions, and 134 is both SIGABRT and the quota error, so only these words name a cause.
    // Bad audio is named last and only by the words that were seen for it, so a new wording reads EngineCrashed and never
    // asks a person to record again.
    private static readonly (string Words, RecordingProblem Problem)[] DecodeErrors =
    [
        ("No space left on device", RecordingProblem.DiskFull),
        ("Disk quota exceeded", RecordingProblem.DiskFull),
        ("Read-only file system", RecordingProblem.DiskRefused),
        ("Permission denied", RecordingProblem.DiskRefused),
        ("Error opening input files: Invalid data found when processing input", RecordingProblem.AudioUnreadable),
        ("Error opening input files: End of file", RecordingProblem.AudioUnreadable),
        ("no decoder found for", RecordingProblem.AudioUnreadable),
        ("does not contain any stream", RecordingProblem.AudioUnreadable),
        ("Not yet implemented in FFmpeg", RecordingProblem.AudioUnreadable),
        ("Error while opening decoder: Function not implemented", RecordingProblem.AudioUnreadable),
        ("Task finished with error code: -22 (Invalid argument)", RecordingProblem.AudioUnreadable),
    ];

    /// <summary>
    /// The path of the WAV file. A file from an earlier attempt is used again. An audio file never changes after its join, and
    /// a late part (REC-24) writes a new one with a new decoded copy, so the copy always matches its audio.
    /// </summary>
    public async Task<string> PrepareAsync(TranscriptionInput input, CancellationToken cancellationToken)
    {
        var wav = input.WorkAudioPath;
        if (!File.Exists(wav))
            await DecodeAsync(input.AudioPath, wav, wav + RecordingFiles.PartialSuffix, cancellationToken);

        var samples = new FileInfo(wav).Length / BytesPerSample;
        if (samples > Recording.MaxDuration.TotalSeconds * Recording.SamplesPerSecond)
            throw new TranscriptionEngineException(RecordingProblem.TooLong);

        return wav;
    }

    private async Task DecodeAsync(string audio, string wav, string partial, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var longest = ((int)Recording.MaxDuration.TotalSeconds + 1).ToString(CultureInfo.InvariantCulture);
        string[] command =
        [
            settings.FfmpegPath, "-nostdin", "-y", "-v", "error", "-threads", "1", "-i", audio, "-t", longest,
            "-map_metadata", "-1", "-bitexact", "-ar", Recording.SamplesPerSecond.ToString(CultureInfo.InvariantCulture),
            "-ac", "1", "-c:a", "pcm_s16le", "-f", "wav", partial,
        ];

        var oomKillsBefore = ChildProcess.ReadOomKills(settings.MemoryEventsPath);
        using var process = ChildProcess.Start(command, threads: 1);
        using var killOnCancel = cancellationToken.Register(() => ChildProcess.KillQuietly(process));
        process.StandardInput.Close();
        var discardOutput = ChildProcess.DiscardAsync(process.StandardOutput);
        var errorTail = await ChildProcess.ReadTailAsync(process.StandardError);
        await discardOutput;
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
        {
            DeleteLeftover(partial);
            throw new TranscriptionEngineException(process.ExitCode == ChildProcess.SigkillExitCode
                ? ChildProcess.ProblemOfKill(settings.MemoryEventsPath, oomKillsBefore)
                : ProblemOfDecodeError(errorTail));
        }

        File.Move(partial, wav, overwrite: true);
    }

    // A disk that refused the decode can also refuse this delete. The sweep at the next start removes the file, and the
    // cause of the failure is the answer that a person needs.
    private static void DeleteLeftover(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static RecordingProblem ProblemOfDecodeError(string errorTail)
    {
        foreach (var (words, problem) in DecodeErrors)
        {
            if (errorTail.Contains(words, StringComparison.Ordinal))
                return problem;
        }

        return RecordingProblem.EngineCrashed;
    }
}
