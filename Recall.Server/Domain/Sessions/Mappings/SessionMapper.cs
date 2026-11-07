namespace Recall.Server.Domain.Sessions.Mappings;

using Dtos;
using Riok.Mapperly.Abstractions;
using TimeZones;

[Mapper]
public static partial class SessionMapper
{
    public static SessionDto ToSessionDto(this Session session, TimeZoneId zone)
        => session.ToDtoWithoutDerivedValues() with
        {
            JournalingDay = zone.ToLocal(session.StartedAt).Date,
            Topics = TopicsOf(session.UserTopics, session.AiTopics),
        };

    public static IReadOnlyList<SessionTopicDto> TopicsOf(IEnumerable<string> userTopics, IEnumerable<string> aiTopics)
        => userTopics.Select(name => new SessionTopicDto { Name = name, Provenance = nameof(TopicProvenance.UserSet) })
            .Concat(aiTopics.Select(name => new SessionTopicDto { Name = name, Provenance = nameof(TopicProvenance.AiSuggested) }))
            .ToList();

    public static DraftSaveResultDto ToSaveResult(this Session session, DraftSlot slot, DraftDecision decision) => new()
    {
        Outcome = decision.Outcome.ToString(),
        DraftRevision = decision.NextBase,
        LatestRevision = slot.LatestRevision,
        SavedAt = slot.SavedAt,
        CleanupStatus = session.CleanupStatus.ToString(),
        Version = session.Version,
    };

    [MapProperty(nameof(Session.MentionedPersonIds), nameof(SessionDto.PersonIds))]
    [MapperIgnoreTarget(nameof(SessionDto.JournalingDay))]
    [MapperIgnoreTarget(nameof(SessionDto.Topics))]
    [MapperIgnoreSource(nameof(Session.TenantId))]
    [MapperIgnoreSource(nameof(Session.LastTranscriptRevision))]
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
