namespace Recall.Server.Databases;

using Domain.Users;
using Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;
using Resources;

/// <summary>
/// A personal access token that cannot write the journal saves no change to a journal row. The journal types are the
/// types whose root declares the <see cref="QueryFilterNames.JournalOwner"/> filter, so a new journal table needs no
/// edit here. The rule sees the tracker after the domain event handlers, so it also covers the rows that they change.
/// </summary>
internal static class JournalWriteRule
{
    internal const string Refusal = "This personal access token can read your journal but cannot change it.";

    /// <exception cref="ForbiddenAccessException">The caller cannot write the journal, and an entry changes a journal row.</exception>
    public static void Check(CallerIdentity caller, IEnumerable<EntityEntry> entries)
    {
        if (caller is CallerIdentity.PersonalAccessToken { CanWriteJournal: false }
            && entries.Any(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted && IsJournal(e.Metadata)))
            throw new ForbiddenAccessException(Refusal);
    }

    public static bool IsJournal(IReadOnlyEntityType type)
        => type.GetRootType().FindDeclaredQueryFilter(QueryFilterNames.JournalOwner) is not null;
}
