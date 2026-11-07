namespace Recall.Server.Domain.Recordings.Transcription;

public sealed class TranscriptionOptions
{
    public const string SectionName = "Transcription";

    public string ModelRoot { get; init; } = "/data/models";

    public string ModelBaseUrl { get; init; } = ModelManifest.BaseUrl;

    public string MemoryEventsPath { get; init; } = "/sys/fs/cgroup/memory.events";

    public string FfmpegPath { get; init; } = "/usr/local/bin/ffmpeg";

    public string WorkerPath { get; init; } = Path.Combine(AppContext.BaseDirectory, "transcriber", "Recall.Transcriber.dll");

    public int Threads { get; init; } = 2;

    public int ModelRetryMinutes { get; init; } = 15;

    public int StallMinutes { get; init; } = 10;

    /// <summary>
    /// The largest file of one part that goes to a cloud provider. OpenAI and Groq take files of up to 25 MB, and no page says
    /// whether a MB is 10^6 or 2^20 bytes, so the smaller one holds. A part of 16 kHz mono PCM of this size is about 13 minutes.
    /// </summary>
    public long CloudPartBytes { get; init; } = 25_000_000;
}
