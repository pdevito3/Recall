namespace Recall.Server.Resources.Storage;

using Domain;
using Exceptions;

/// <summary>
/// The relative path of one file or one folder in the <see cref="FileStore"/>, with <c>/</c> between its segments.
/// A key cannot leave the root and cannot name a temporary file.
/// </summary>
public sealed partial class FileKey : ValueObject
{
    internal const string TemporaryFolder = ".tmp";

    private const char Separator = '/';

    public string Value { get; }

    public FileKey(string value)
    {
        if (string.IsNullOrEmpty(value))
            throw new InvalidFileKeyException("A file key cannot be empty.");
        if (value.Contains('\0'))
            throw new InvalidFileKeyException("A file key cannot have a NUL character.");
        if (value[0] == Separator || value.Contains('\\') || Path.IsPathRooted(value))
            throw new InvalidFileKeyException("A file key must be a relative path that uses a forward slash.");

        foreach (var segment in value.Split(Separator))
        {
            if (segment.Length == 0)
                throw new InvalidFileKeyException("A file key cannot have an empty segment.");
            if (segment is "." or "..")
                throw new InvalidFileKeyException("A file key cannot have a segment that points to a folder.");
            // A file system on a Mac or on Windows does not see the case of a name
            if (segment.Equals(TemporaryFolder, StringComparison.OrdinalIgnoreCase))
                throw new InvalidFileKeyException("A file key cannot name the folder of the temporary files.");
        }

        Value = value;
    }

    public static FileKey Of(string value) => new(value);

    /// <summary>
    /// The key of a path in the folder of a tenant, or the key of that folder with no path. All the files of a tenant
    /// must have a key from this method, because a tenant delete deletes this folder.
    /// </summary>
    public static FileKey ForTenant(string tenantId, params ReadOnlySpan<string> path)
        => new(string.Join(Separator, ["tenants", tenantId, .. path]));

    internal string[] Segments => Value.Split(Separator);

    public override string ToString() => Value;
}
