namespace Recall.Server.Domain.Sessions.Mappings;

using Content;
using Dtos;
using People;
using Riok.Mapperly.Abstractions;

[Mapper]
public static partial class SessionMapper
{
    /// <summary>The DTO with the matches that each proposal has in <paramref name="people"/> now.</summary>
    public static SessionDto ToSessionDto(this Session session, ProposalPeople people)
        => session.ToDtoWithoutDerivedValues() with
        {
            Topics = TopicsOf(session.UserTopics, session.AiTopics),
            CleanupFailure = session.CleanupFailure.ToString(),
            Suggestions = SuggestionsOf(session),
            PeopleProposals = ProposalsOf(session, people.People),
        };

    private static List<SessionSuggestionDto> SuggestionsOf(Session session)
        => session.TopicSuggestions.Select(value => new SessionSuggestionDto { Kind = nameof(SuggestionKind.Topic), Value = value })
            .Concat(session.MoodSuggestions.Select(value => new SessionSuggestionDto { Kind = nameof(SuggestionKind.Mood), Value = value }))
            .ToList();

    private static List<PersonProposalDto> ProposalsOf(Session session, IReadOnlyCollection<Person> people)
    {
        if (session.PeopleProposals.Count == 0)
            return [];

        var tagged = PersonMentions.PersonIdsIn(session.Cleaned.Text);
        var plain = ProseMirrorToPlainText.Render(session.Cleaned.Text);
        return session.PeopleProposals
            .Select(proposal => (proposal, matches: proposal.MatchesIn(people)))
            .Where(pair => !PersonProposal.IsSettledBy(tagged, pair.matches))
            .Select(pair => new PersonProposalDto
            {
                Label = pair.proposal.Label,
                MatchedPersonIds = pair.matches.Select(person => person.Id).ToList(),
                IsNew = pair.matches.Count == 0,
                Contexts = PeopleTagSpans.Contexts(plain, pair.proposal.Label),
            }).ToList();
    }

    public static IReadOnlyList<SessionTopicDto> TopicsOf(IEnumerable<string> userTopics, IEnumerable<string> aiTopics)
        => userTopics.Select(name => new SessionTopicDto { Name = name, Provenance = nameof(TopicProvenance.UserSet) })
            .Concat(aiTopics.Select(name => new SessionTopicDto { Name = name, Provenance = nameof(TopicProvenance.AiSuggested) }))
            .ToList();

    public static DraftSaveResultDto ToSaveResult(this Session session, DraftSlot slot, DraftDecision decision) => new()
    {
        Outcome = decision.Outcome.ToString(),
        DraftRevision = decision.DraftRevision,
        LatestRevision = slot.LatestRevision,
        SavedAt = slot.SavedAt,
        CleanupStatus = session.CleanupStatus.ToString(),
        Version = session.Version,
    };

    [MapProperty(nameof(Session.MentionedPersonIds), nameof(SessionDto.PersonIds))]
    [MapperIgnoreTarget(nameof(SessionDto.Topics))]
    [MapperIgnoreTarget(nameof(SessionDto.CleanupFailure))]
    [MapperIgnoreTarget(nameof(SessionDto.Suggestions))]
    [MapperIgnoreTarget(nameof(SessionDto.PeopleProposals))]
    [MapperIgnoreSource(nameof(Session.CleanupFailure))]
    [MapperIgnoreSource(nameof(Session.CleanupRunId))]
    [MapperIgnoreSource(nameof(Session.ImportBatchId))]
    [MapperIgnoreSource(nameof(Session.TopicSuggestions))]
    [MapperIgnoreSource(nameof(Session.MoodSuggestions))]
    [MapperIgnoreSource(nameof(Session.PeopleProposals))]
    [MapperIgnoreSource(nameof(Session.TimeZone))]
    [MapperIgnoreSource(nameof(Session.TenantId))]
    [MapperIgnoreSource(nameof(Session.UserId))]
    [MapperIgnoreSource(nameof(Session.Preview))]
    [MapperIgnoreSource(nameof(Session.CleanupRun))]
    [MapperIgnoreSource(nameof(Session.UserTopics))]
    [MapperIgnoreSource(nameof(Session.AiTopics))]
    [MapperIgnoreSource(nameof(Session.LastModifiedOn))]
    [MapperIgnoreSource(nameof(Session.IsDeleted))]
    [MapperIgnoreSource(nameof(Session.DomainEvents))]
    private static partial SessionDto ToDtoWithoutDerivedValues(this Session session);
}
