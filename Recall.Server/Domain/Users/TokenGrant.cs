namespace Recall.Server.Domain.Users;

using System.Collections.Frozen;
using Ardalis.SmartEnum;
using Exceptions;

/// <summary>What a personal access token may do with the journal of its person.</summary>
public sealed class JournalAccess : SmartEnum<JournalAccess>
{
    public static readonly JournalAccess None = new("none", 0);
    public static readonly JournalAccess Read = new("read", 1);
    public static readonly JournalAccess ReadWrite = new("read_write", 2);

    private JournalAccess(string name, int value) : base(name, value) { }

    public bool CanRead => this != None;

    public bool CanWrite => this == ReadWrite;

    /// <exception cref="ValidationException">
    /// <paramref name="write"/> without <paramref name="read"/>. An update or a delete loads its row through the read
    /// filter, so write alone is not a value.
    /// </exception>
    public static JournalAccess Of(bool read, bool write) => (read, write) switch
    {
        (true, true) => ReadWrite,
        (true, false) => Read,
        (false, false) => None,
        (false, true) => throw new ValidationException("journal", "A token that changes your journal must also read it."),
    };
}

/// <summary>
/// The most that a personal access token can do, fixed when the token is made. The token holds only the permissions
/// that both its person and <see cref="Permissions"/> hold.
/// </summary>
public sealed record TokenGrant
{
    public TokenGrant(IEnumerable<Permission> permissions, JournalAccess journal)
    {
        Permissions = permissions.ToFrozenSet();
        Journal = journal;
    }

    public IReadOnlySet<Permission> Permissions { get; }

    public JournalAccess Journal { get; }

    /// <summary>The text of the grant, for example <c>read:manage_users,read_users</c>.</summary>
    public string Encode()
        => $"{Journal.Name}:{string.Join(',', Permissions.Select(p => p.Value).Order(StringComparer.Ordinal))}";

    /// <summary>
    /// Reads the text of <see cref="Encode"/>. A permission name that this build does not know is dropped, and a
    /// journal access that it does not know is <see cref="JournalAccess.None"/>, so a removed name grants nothing.
    /// </summary>
    public static TokenGrant Decode(string text)
    {
        var (journal, permissions) = text.IndexOf(':') is var colon and >= 0
            ? (text[..colon], text[(colon + 1)..])
            : (text, "");
        var known = Permission.ListNames().ToHashSet(StringComparer.Ordinal);
        return new TokenGrant(
            permissions.Split(',', StringSplitOptions.RemoveEmptyEntries).Where(known.Contains).Select(Permission.Of),
            JournalAccess.TryFromName(journal, out var access) ? access : JournalAccess.None);
    }

    public bool Equals(TokenGrant? other)
        => other is not null && Journal == other.Journal && Permissions.SetEquals(other.Permissions);

    public override int GetHashCode()
        => HashCode.Combine(Journal, Permissions.Aggregate(0, (hash, p) => hash ^ p.GetHashCode()));
}
