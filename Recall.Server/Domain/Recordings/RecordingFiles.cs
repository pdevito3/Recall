namespace Recall.Server.Domain.Recordings;

using Resources.Storage;

public static class RecordingFiles
{
    public const string RootFolder = "recordings";

    public const int SliceSignatureBytes = 8;

    private const string SlicesFolder = "slices";

    private const string WorkAudioExtension = ".wav";

    public const string PartialSuffix = ".part";

    public static FileKey OwnerFolder(string tenantId, string userId) => FileKey.ForTenant(tenantId, RootFolder, userId);

    public static FileKey Folder(string tenantId, string userId, string recordingId)
        => FileKey.ForTenant(tenantId, RootFolder, userId, recordingId);

    public static FileKey Folder(Recording recording) => Folder(recording.TenantId, recording.UserId, recording.Id);

    public static FileKey Slices(Recording recording)
        => FileKey.ForTenant(recording.TenantId, RootFolder, recording.UserId, recording.Id, SlicesFolder);

    public static FileKey Slice(Recording recording, int index, string sha256)
        => FileKey.ForTenant(recording.TenantId, RootFolder, recording.UserId, recording.Id, SlicesFolder, $"{index:D6}-{sha256}");

    /// <summary>
    /// The joined audio that the row names. Each read of the audio uses this key. A Recording with no joined audio gets the
    /// download name, which holds no file, because each join writes a file of its own name.
    /// </summary>
    public static FileKey Audio(Recording recording) => Audio(recording, AudioNameOf(recording));

    public static FileKey Audio(Recording recording, string audioFile) => InFolder(recording, audioFile);

    /// <summary>The decoded copy of the audio that the row names. A new audio file gets a new decoded copy, so a decode never mixes two audio files.</summary>
    public static FileKey WorkAudio(Recording recording) => WorkAudio(recording, AudioNameOf(recording));

    public static FileKey WorkAudio(Recording recording, string audioFile)
        => InFolder(recording, Path.ChangeExtension(audioFile, WorkAudioExtension));

    public static FileKey PartialWorkAudio(Recording recording) => PartialWorkAudio(recording, AudioNameOf(recording));

    public static FileKey PartialWorkAudio(Recording recording, string audioFile)
        => InFolder(recording, Path.ChangeExtension(audioFile, WorkAudioExtension) + PartialSuffix);

    private static string AudioNameOf(Recording recording) => recording.AudioFile ?? recording.AudioFileName;

    private static FileKey InFolder(Recording recording, string name)
        => FileKey.ForTenant(recording.TenantId, RootFolder, recording.UserId, recording.Id, name);

    public static bool StartsAsFormat(RecordingFormat format, ReadOnlySpan<byte> bytes) => format switch
    {
        RecordingFormat.Webm => bytes is [0x1A, 0x45, 0xDF, 0xA3, ..],
        RecordingFormat.Mp4 => bytes.Length >= SliceSignatureBytes && bytes[4..8].SequenceEqual("ftyp"u8),
        _ => false,
    };
}
