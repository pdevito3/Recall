namespace Recall.Server.Domain.ImportBatches;

using Exceptions;
using Sessions;

/// <summary>One entry of a put, parsed alone, so that an entry that is not valid gets <c>Invalid</c> and the others go on (IMP-28).</summary>
public sealed record ImportPutEntry(string ClientSessionId, ImportAgentLayer? Agent, string? Field, string? Reason)
{
    public static ImportPutEntry Parse(string clientSessionId, Func<ImportAgentLayer> agent)
    {
        try
        {
            Session.CheckClientSessionId(clientSessionId);
            return new ImportPutEntry(clientSessionId, agent(), null, null);
        }
        catch (ValidationException refusal)
        {
            return new ImportPutEntry(clientSessionId, null, refusal.Property, refusal.Message);
        }
    }
}

/// <summary>The outcome of one entry of a put. A <c>PositionTaken</c> names the entry that holds the position (IMP-8).</summary>
public sealed record ImportPutResult(
    string ClientSessionId, ImportPutOutcome Outcome, string? Field = null, string? Reason = null, string? PositionTakenBy = null);

/// <summary>The outcomes of a put, in the order of the call, and the new entries that the caller adds.</summary>
public sealed record ImportPut(IReadOnlyList<ImportPutResult> Results, IReadOnlyList<ImportEntry> Added);
