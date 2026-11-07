namespace Recall.Server.Domain.Recordings.Transcription;

using Jobs;

public static class TranscriptionRegistration
{
    public static void AddTranscription(this WebApplicationBuilder builder)
    {
        builder.Services.Configure<TranscriptionOptions>(builder.Configuration.GetSection(TranscriptionOptions.SectionName));
        builder.Services.AddSingleton<ModelStore>();
        builder.Services.AddSingleton<ModelDownloads>();
        builder.Services.AddSingleton<RecordingSweep>();
        builder.Services.AddSingleton<ITranscriptionEngine, LocalParakeetEngine>();
        builder.Services.AddHttpClient(DownloadModelJob.HttpClientName, client => client.Timeout = Timeout.InfiniteTimeSpan);
    }
}
