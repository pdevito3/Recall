namespace Recall.Server.Domain.Sessions;

public sealed record CleanupOutcome(
    string CleanedText,
    string Synopsis,
    IReadOnlyList<string> TopicSuggestions,
    IReadOnlyList<string> MoodSuggestions,
    IReadOnlyList<PersonProposal> PeopleProposals);
