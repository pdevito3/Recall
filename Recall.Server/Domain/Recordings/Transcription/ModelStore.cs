namespace Recall.Server.Domain.Recordings.Transcription;

using Microsoft.Extensions.Options;

public sealed record ModelFile(string Name, long ByteLength, string Sha256);

public static class ModelManifest
{
    public const string FolderName = "parakeet-tdt-0.6b-v2-int8";

    public const string BaseUrl =
        "https://huggingface.co/csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8/resolve/1ab9323565ddb038682214b292f588070a538ce2";

    public static readonly IReadOnlyList<ModelFile> Files =
    [
        new("tokens.txt", 9_384, "ec182b70dd42113aff6c5372c75cac58c952443eb22322f57bbd7f53977d497d"),
        new("joiner.int8.onnx", 1_739_080, "7946164367946e7f9f29a122407c3252b680dbae9a51343eb2488d057c3c43d2"),
        new("decoder.int8.onnx", 7_257_753, "b6bb64963457237b900e496ee9994b59294526439fbcc1fecf705b31a15c6b4e"),
        new("encoder.int8.onnx", 652_184_296, "a32b12d17bbbc309d0686fbbcc2987b5e9b8333a7da83fa6b089f0a2acd651ab"),
    ];
}

public enum ModelStatus
{
    Missing,
    Downloading,
    Ready,
    DownloadFailed,
    HashRejected,
}

public sealed class ModelStore(IOptions<TranscriptionOptions> options)
{
    private const string AllHashesPassedMarker = ".verified";

    private readonly Lock _gate = new();
    private ModelStatus _lastFault = ModelStatus.Missing;
    private bool _isDownloading;
    private bool _downloadIsQueued;
    private DateTimeOffset? _lastTry;

    public string Folder => Path.Combine(options.Value.ModelRoot, ModelManifest.FolderName);

    public bool IsReady
        => File.Exists(MarkerPath)
            && ModelManifest.Files.All(file => new FileInfo(PathOf(file)) is { Exists: true } info && info.Length == file.ByteLength);

    public ModelStatus Status
    {
        get
        {
            if (IsReady)
                return ModelStatus.Ready;

            lock (_gate)
                return _isDownloading ? ModelStatus.Downloading : _lastFault;
        }
    }

    public string PathOf(ModelFile file) => Path.Combine(Folder, file.Name);

    public void MarkVerified()
    {
        using (var marker = new FileStream(MarkerPath, FileMode.Create, FileAccess.Write))
            marker.Flush(flushToDisk: true);

        lock (_gate)
            _lastFault = ModelStatus.Missing;
    }

    public bool TryReserveDownload()
    {
        lock (_gate)
        {
            if (_downloadIsQueued)
                return false;

            _downloadIsQueued = true;
            return true;
        }
    }

    public bool TryBeginDownload(DateTimeOffset now, TimeSpan retryGap)
    {
        lock (_gate)
        {
            if (_lastTry is { } last && now - last < retryGap / 2)
                return false;

            _lastTry = now;
            _isDownloading = true;
            return true;
        }
    }

    public void EndDownload(ModelStatus fault, bool retryIsQueued)
    {
        lock (_gate)
        {
            _lastFault = fault;
            _isDownloading = false;
            _downloadIsQueued = retryIsQueued;
        }
    }

    private string MarkerPath => Path.Combine(Folder, AllHashesPassedMarker);
}
