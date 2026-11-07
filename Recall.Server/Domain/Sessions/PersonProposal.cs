namespace Recall.Server.Domain.Sessions;

/// <summary>A name that Cleanup proposes as a mention. A null person means that the directory has no match.</summary>
public sealed record PersonProposal(string Label, string? MatchedPersonId);
