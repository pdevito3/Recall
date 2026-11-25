namespace Recall.Server.Domain.ImportBatches;

using Exceptions;
using Sessions;

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

public sealed record ImportPutResult(
    string ClientSessionId, ImportPutOutcome Outcome, string? Field = null, string? Reason = null, string? PositionTakenBy = null);

public sealed record ImportPut(IReadOnlyList<ImportPutResult> Results, IReadOnlyList<ImportEntry> Added);
