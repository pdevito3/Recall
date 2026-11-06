namespace Recall.Server.Domain.Recordings.Transcription;

public sealed class TranscriptionOptions
{
    public const string SectionName = "Transcription";

    /// <summary>The folder that holds the speech model. The image carries no model.</summary>
    public string ModelRoot { get; init; } = "/data/models";

    /// <summary>Where the files of the model come from. The pinned hashes in <see cref="ModelManifest"/> decide if they are good.</summary>
    public string ModelBaseUrl { get; init; } = ModelManifest.BaseUrl;

    public string FfmpegPath { get; init; } = "/usr/local/bin/ffmpeg";

    /// <summary>The worker that loads the model. A path that ends in <c>.dll</c> runs on the dotnet host of this server.</summary>
    public string WorkerPath { get; init; } = Path.Combine(AppContext.BaseDirectory, "transcriber", "Recall.Transcriber.dll");

    /// <summary>The CPU threads of one transcription. A NAS has other work, so the default stays low.</summary>
    public int Threads { get; init; } = 2;

    /// <summary>The wait before a failed model download runs again, only while a Recording waits for the model.</summary>
    public int ModelRetryMinutes { get; init; } = 15;

    /// <summary>The longest time that the worker can go without a chunk before the job stops it.</summary>
    public int StallMinutes { get; init; } = 10;
}
