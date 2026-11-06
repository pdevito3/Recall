namespace Recall.Server.Domain.Recordings;

using Resources.Storage;

/// <summary>Each file of a Recording lives in the folder of its tenant, so a tenant delete takes it (R5).</summary>
public static class RecordingFiles
{
    public const string RootFolder = "recordings";

    public const int SliceSignatureBytes = 8;

    private const string SlicesFolder = "slices";

    public static FileKey OwnerFolder(string tenantId, string userId) => FileKey.ForTenant(tenantId, RootFolder, userId);

    public static FileKey Folder(string tenantId, string userId, string recordingId)
        => FileKey.ForTenant(tenantId, RootFolder, userId, recordingId);

    public static FileKey Folder(Recording recording) => Folder(recording.TenantId, recording.UserId, recording.Id);

    public static FileKey Slices(Recording recording)
        => FileKey.ForTenant(recording.TenantId, RootFolder, recording.UserId, recording.Id, SlicesFolder);

    // The hash in the name means that a write that lost a race never replaces the bytes of the winner.
    public static FileKey Slice(Recording recording, int index, string sha256)
        => FileKey.ForTenant(recording.TenantId, RootFolder, recording.UserId, recording.Id, SlicesFolder, $"{index:D6}-{sha256}");

    public static FileKey WorkAudio(Recording recording)
        => FileKey.ForTenant(recording.TenantId, RootFolder, recording.UserId, recording.Id, "work.wav");

    public static FileKey Audio(Recording recording)
        => FileKey.ForTenant(recording.TenantId, RootFolder, recording.UserId, recording.Id, recording.AudioFileName);

    /// <summary>True when the first bytes of an upload are the container that the Recording names.</summary>
    public static bool StartsAsFormat(RecordingFormat format, ReadOnlySpan<byte> bytes) => format switch
    {
        RecordingFormat.Webm => bytes is [0x1A, 0x45, 0xDF, 0xA3, ..],
        RecordingFormat.Mp4 => bytes.Length >= SliceSignatureBytes && bytes[4..8].SequenceEqual("ftyp"u8),
        _ => false,
    };
}
