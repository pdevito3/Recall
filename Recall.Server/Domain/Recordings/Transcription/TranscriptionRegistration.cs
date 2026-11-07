namespace Recall.Server.Domain.Recordings.Transcription;

using Jobs;

public static class TranscriptionRegistration
{
    public static void AddTranscription(this WebApplicationBuilder builder)
    {
        builder.Services.AddOptions<TranscriptionOptions>()
            .BindConfiguration(TranscriptionOptions.SectionName)
            .Validate(o => o.ModelRetryMinutes >= 1,
                $"{TranscriptionOptions.SectionName}:ModelRetryMinutes must be 1 or more. A lower value sends a download of the speech model again with no wait.")
            .ValidateOnStart();
        builder.Services.AddSingleton<ModelStore>();
        builder.Services.AddSingleton<ModelDownloads>();
        builder.Services.AddSingleton<RecordingSweep>();
        builder.Services.AddSingleton<ITranscriptionEngine, LocalParakeetEngine>();
        builder.Services.AddHttpClient(DownloadModelJob.HttpClientName, client => client.Timeout = Timeout.InfiniteTimeSpan);
    }
}
