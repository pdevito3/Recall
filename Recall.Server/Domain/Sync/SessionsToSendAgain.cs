namespace Recall.Server.Domain.Sync;

using People;
using Sessions;

public static class SessionsToSendAgain
{
    /// <summary>
    /// SES-56: the Sessions that the device holds and that a changed Person joins as a match, so that the device gets the matches
    /// of now. A Person that leaves a match sends nothing: the device drops a gone Person from its list, and an approve that binds a
    /// renamed Person still binds her.
    /// </summary>
    public static IReadOnlyList<string> Of(IEnumerable<(string SessionId, IReadOnlyList<PersonProposal> Proposals)> held,
        IReadOnlyCollection<Person> changed)
        => held.Where(session => session.Proposals.Any(proposal => changed.Any(proposal.Matches)))
            .Select(session => session.SessionId)
            .ToList();
}
