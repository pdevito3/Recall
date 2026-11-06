namespace Recall.Server.Domain.Recordings;

[NotAudited("One part of the upload of a Recording. A slice is written once and goes with its Recording, whose row has the version and the audit.")]
public class RecordingSlice
{
    public const int Sha256HexLength = 64;

    public string RecordingId { get; private set; } = default!;

    public int Index { get; private set; }

    public long ByteLength { get; private set; }

    public string Sha256 { get; private set; } = default!;

    public Recording Recording { get; private set; } = default!;

    internal RecordingSlice(string recordingId, int index, long byteLength, string sha256)
    {
        RecordingId = recordingId;
        Index = index;
        ByteLength = byteLength;
        Sha256 = sha256;
    }

    private RecordingSlice()
    {
    }

    public static RecordingSlice Of(Recording recording, int index, long byteLength, string sha256)
        => new(recording.Id, index, byteLength, sha256);
}
