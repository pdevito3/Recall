namespace Recall.Server.Domain.Recordings.Transcription;

public sealed class TranscriptionOptions
{
    public const string SectionName = "Transcription";

    public string ModelRoot { get; init; } = "/data/models";

    public string ModelBaseUrl { get; init; } = ModelManifest.BaseUrl;

    public string FfmpegPath { get; init; } = "/usr/local/bin/ffmpeg";

    public string WorkerPath { get; init; } = Path.Combine(AppContext.BaseDirectory, "transcriber", "Recall.Transcriber.dll");

    public int Threads { get; init; } = 2;

    public int ModelRetryMinutes { get; init; } = 15;

    public int StallMinutes { get; init; } = 10;
}
