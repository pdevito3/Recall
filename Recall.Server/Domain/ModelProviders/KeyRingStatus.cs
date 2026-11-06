namespace Recall.Server.Domain.ModelProviders;

public enum KeyRingState
{
    Same = 0,
    Changed = 1,
    NotWritable = 2,
}

/// <summary>What the start of this process saw of the key ring.</summary>
public sealed class KeyRingStatus
{
    public const string HealthCheckName = "key-ring";
    public const string ChangedText = "The key ring changed: the database holds values that this key ring cannot read. Restore the key folder from the same backup as the database, or enter each API key of a model provider again.";
    public const string NotWritableText = "The key folder takes no write, so the next start makes a new key ring, and each saved API key of a model provider stops working. Make the folder writable.";

    public bool FolderIsWritable { get; set; } = true;

    public bool RingChanged { get; set; }

    public static KeyRingState StateFor(bool folderIsWritable, bool anyKeyIsUnreadable)
    {
        if (!folderIsWritable)
            return KeyRingState.NotWritable;

        return anyKeyIsUnreadable ? KeyRingState.Changed : KeyRingState.Same;
    }

    public string? Warning => !FolderIsWritable ? NotWritableText : RingChanged ? ChangedText : null;
}
