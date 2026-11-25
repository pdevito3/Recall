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
/// that both its person and <see cref="Permissions"/> hold. <see cref="JobTools"/> lets it reach the job tools at
/// <c>/backwave-mcp</c>, and only while its person is the Owner.
/// </summary>
public sealed record TokenGrant
{
    private const string JobToolsItem = "job_tools";

    private static readonly FrozenSet<string> KnownPermissions = Permission.ListNames().ToFrozenSet(StringComparer.Ordinal);

    public TokenGrant(IEnumerable<Permission> permissions, JournalAccess journal, bool jobTools = false)
    {
        Permissions = permissions.ToFrozenSet();
        Journal = journal;
        JobTools = jobTools;
    }

    public IReadOnlySet<Permission> Permissions { get; }

    public JournalAccess Journal { get; }

    /// <summary>The token may use the job tools of the Owner.</summary>
    public bool JobTools { get; }

    /// <summary>
    /// The text of the grant, for example <c>read:manage_users,read_users</c>, or <c>read:read_users:job_tools</c> with
    /// <see cref="JobTools"/>.
    /// </summary>
    public string Encode()
    {
        var text = $"{Journal.Name}:{string.Join(',', Permissions.Select(p => p.Value).Order(StringComparer.Ordinal))}";
        return JobTools ? $"{text}:{JobToolsItem}" : text;
    }

    /// <summary>
    /// Reads the text of <see cref="Encode"/>. A permission name or an item that this build does not know is dropped, and a
    /// journal access that it does not know is <see cref="JournalAccess.None"/>, so a removed name grants nothing. A text
    /// with no third part holds no job tools.
    /// </summary>
    public static TokenGrant Decode(string text)
    {
        var parts = text.Split(':', 3);
        var journal = parts[0];
        var permissions = parts.Length > 1 ? parts[1] : "";
        var items = parts.Length > 2 ? parts[2].Split(',', StringSplitOptions.RemoveEmptyEntries) : [];
        return new TokenGrant(
            permissions.Split(',', StringSplitOptions.RemoveEmptyEntries).Where(KnownPermissions.Contains).Select(Permission.Of),
            JournalAccess.TryFromName(journal, out var access) ? access : JournalAccess.None,
            items.Contains(JobToolsItem, StringComparer.Ordinal));
    }

    public bool Equals(TokenGrant? other)
        => other is not null && Journal == other.Journal && JobTools == other.JobTools && Permissions.SetEquals(other.Permissions);

    public override int GetHashCode()
        => HashCode.Combine(Journal, JobTools, Permissions.Aggregate(0, (hash, p) => hash ^ p.GetHashCode()));
}
