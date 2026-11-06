namespace Recall.Server.Domain.Sessions;

using System.Text;
using Content;
using Exceptions;
using Metadata;

public class Session : BaseEntity, ITenantable, IJournalOwned
{
    public const string IdPrefix = "ses";
    public const int ClientSessionIdMaxLength = 100;
    public const int DraftMaxLength = 1_000_000;
    public const int PreviewMaxLength = 200;
    public const int SynopsisMaxLength = 10_000;
    public const int MetadataValueMaxLength = 100;
    public const int MetadataListMaxCount = 50;

    // A JSON list of the longest values, with room for the quotes and the commas, and for the escapes of each character.
    public const int MetadataListColumnMaxLength = MetadataListMaxCount * (MetadataValueMaxLength * 6 + 3);

    private const string ClientSessionIdField = "clientSessionId";
    private const string CleanupField = "cleanup";
    private const string SuggestionField = "value";
    private const string CleanupRunIdPrefix = "run";

    private readonly List<RawRevision> _appendedRawRevisions = [];
    private readonly List<CleanedRevision> _appendedCleanedRevisions = [];

    [NotSensitive] public string TenantId { get; private set; } = default!;

    [NotSensitive] public string UserId { get; private set; } = default!;

    [NotSensitive] public string ClientSessionId { get; private set; } = default!;

    [Sensitive] public DateTimeOffset StartedAt { get; private set; }

    [Sensitive] public DraftSlot Raw { get; private set; } = DraftSlot.Empty;

    [Sensitive] public DraftSlot Cleaned { get; private set; } = DraftSlot.Empty;

    [Sensitive] public string Preview { get; private set; } = string.Empty;

    public bool CleanedHasHandEdits { get; private set; }

    [Sensitive] public string? Synopsis { get; private set; }

    public CleanupRunState CleanupRun { get; private set; }

    public int? LastCleanupRawRevision { get; private set; }

    /// <summary>The Cleanup that runs or that ran last. A job works only for its own run.</summary>
    [NotSensitive] public string? CleanupRunId { get; private set; }

    public CleanupFailure CleanupFailure { get; private set; }

    [Sensitive] public IReadOnlyList<string> TopicSuggestions { get; private set; } = [];

    [Sensitive] public IReadOnlyList<string> MoodSuggestions { get; private set; } = [];

    [Sensitive] public IReadOnlyList<PersonProposal> PeopleProposals { get; private set; } = [];

    public uint MetadataVersion { get; private set; }

    [Sensitive] public string Activity { get; private set; } = SessionActivity.None;

    [Sensitive] public IReadOnlyList<string> Moods { get; private set; } = [];

    [Sensitive] public IReadOnlyList<string> UserTopics { get; private set; } = [];

    [Sensitive] public IReadOnlyList<string> AiTopics { get; private set; } = [];

    [Sensitive] public IReadOnlyList<string> MentionedPersonIds { get; private set; } = [];

    [Sensitive] public double? Latitude { get; private set; }

    [Sensitive] public double? Longitude { get; private set; }

    public CleanupStatus CleanupStatus => CleanupStatusOf(CleanupRun, Raw.DraftRevision, LastCleanupRawRevision);

    public static CleanupStatus CleanupStatusOf(CleanupRunState run, int rawDraftRevision, int? lastCleanupRawRevision)
    {
        if (run == CleanupRunState.Running)
            return CleanupStatus.Running;

        if (IsStale(rawDraftRevision, lastCleanupRawRevision))
            return CleanupStatus.Stale;

        return run switch
        {
            CleanupRunState.Clean => CleanupStatus.Clean,
            CleanupRunState.Failed => CleanupStatus.Failed,
            _ => CleanupStatus.NotRun,
        };
    }

    public static Session Create(
        string tenantId, string userId, string clientSessionId, DateTimeOffset claimedStartedAt, CapturedLocation? location, DateTimeOffset now)
    {
        ValidationException.ThrowWhenNullOrWhitespace(clientSessionId, ClientSessionIdField, "Please provide a client session ID.");
        ValidationException.ThrowWhenLongerThan(clientSessionId, ClientSessionIdMaxLength, ClientSessionIdField,
            $"The client session ID must not have more than {ClientSessionIdMaxLength} characters.");
        ValidationException.ThrowWhenHasCharacterNotStorable(clientSessionId, ClientSessionIdField,
            "The client session ID has a character that is not allowed.");

        return new Session
        {
            TenantId = tenantId,
            UserId = userId,
            ClientSessionId = clientSessionId,
            StartedAt = StoredInstantOf(claimedStartedAt < now ? claimedStartedAt : now),
            Latitude = location?.Latitude,
            Longitude = location?.Longitude,
        };
    }

    public DraftSaveOutcome SaveRaw(DraftSave save, bool isReplay)
    {
        var decision = Raw.Decide(save, isReplay);
        Raw = decision.Next;
        if (decision.MintedRevision is { } number)
            _appendedRawRevisions.Add(new RawRevision(Id, number, save.SavedAt, save.ClaimedSavedAt, save.Text));

        RefreshFromDrafts();
        return decision.Outcome;
    }

    public DraftSaveOutcome SaveCleaned(DraftSave save, bool isReplay)
    {
        var decision = Cleaned.Decide(save, isReplay);
        Cleaned = decision.Next;
        if (decision.MintedRevision is { } number)
            _appendedCleanedRevisions.Add(new CleanedRevision(Id, number, save.SavedAt, save.ClaimedSavedAt, save.Text));
        if (decision.Outcome == DraftSaveOutcome.Won)
            CleanedHasHandEdits = true;

        RefreshFromDrafts();
        return decision.Outcome;
    }

    public void RenamePerson(string personId, string label, DateTimeOffset now)
    {
        var wasStale = IsStale(Raw.DraftRevision, LastCleanupRawRevision);

        var raw = PersonMentions.Relabel(Raw.Text, personId, label);
        if (raw != Raw.Text)
        {
            Raw = Raw.RewrittenKeepingSavedAt(raw);
            _appendedRawRevisions.Add(new RawRevision(Id, Raw.DraftRevision, now, now, raw));
            if (LastCleanupRawRevision is not null && !wasStale)
                LastCleanupRawRevision = Raw.DraftRevision;
        }

        var cleaned = PersonMentions.Relabel(Cleaned.Text, personId, label);
        if (cleaned != Cleaned.Text)
        {
            Cleaned = Cleaned.RewrittenKeepingSavedAt(cleaned);
            _appendedCleanedRevisions.Add(new CleanedRevision(Id, Cleaned.DraftRevision, now, now, cleaned));
        }

        RefreshFromDrafts();
    }

    public bool SetMetadata(SessionMetadata metadata, uint metadataVersion)
    {
        var isStored = Activity == metadata.Activity && Moods.SequenceEqual(metadata.Moods) && UserTopics.SequenceEqual(metadata.UserTopics);
        if (isStored && metadataVersion != MetadataVersion)
            return false;

        if (metadataVersion != MetadataVersion)
            throw new StaleMetadataVersionException();

        MetadataVersion++;
        Activity = metadata.Activity;
        Moods = metadata.Moods;
        UserTopics = metadata.UserTopics;
        AiTopics = AiTopics.Where(topic => !UserTopics.Contains(topic.Trim(), StringComparer.OrdinalIgnoreCase)).ToList();
        return true;
    }

    /// <summary>The new run goes in the job payload, so a job of an older run cannot end this one.</summary>
    public string BeginCleanup()
    {
        ValidationException.MustNot(CleanupRun == CleanupRunState.Running, CleanupField, "A Cleanup of this Session already runs.");
        CleanupRun = CleanupRunState.Running;
        CleanupRunId = EntityId.New(CleanupRunIdPrefix);
        CleanupFailure = CleanupFailure.None;
        return CleanupRunId;
    }

    public void CompleteCleanup(CleanupOutcome outcome, int rawRevisionRead, DateTimeOffset now)
    {
        RequireRunningCleanup();
        Cleaned = new DraftSlot(outcome.CleanedText, Cleaned.LatestRevision + 1, Cleaned.LatestRevision + 1, now);
        _appendedCleanedRevisions.Add(new CleanedRevision(Id, Cleaned.DraftRevision, now, now, outcome.CleanedText));
        Synopsis = outcome.Synopsis;
        CleanedHasHandEdits = false;
        LastCleanupRawRevision = rawRevisionRead;
        CleanupRun = CleanupRunState.Clean;
        TopicSuggestions = outcome.TopicSuggestions.Where(topic => !HoldsTopic(topic)).ToList();
        MoodSuggestions = outcome.MoodSuggestions.Where(mood => !Moods.Contains(mood, StringComparer.OrdinalIgnoreCase)).ToList();
        PeopleProposals = outcome.PeopleProposals;
        RefreshFromDrafts();
    }

    public void FailCleanup(CleanupFailure failure)
    {
        RequireRunningCleanup();
        CleanupRun = CleanupRunState.Failed;
        CleanupFailure = failure;
    }

    /// <summary>The one writer of AI Topics. It adds no Topic that the User holds (SES-21). A second accept changes nothing.</summary>
    public bool AcceptTopicSuggestion(string topic)
    {
        var offered = TopicSuggestions.FirstOrDefault(t => t.Equals(topic.Trim(), StringComparison.OrdinalIgnoreCase));
        if (offered is null)
            return false;

        TopicSuggestions = TopicSuggestions.Where(t => t != offered).ToList();
        if (HoldsTopic(offered))
            return true;

        ValidationException.Must(AiTopics.Count < MetadataListMaxCount, SuggestionField, $"A Session must not have more than {MetadataListMaxCount} AI Topics.");
        AiTopics = [.. AiTopics, offered];
        MetadataVersion++;
        return true;
    }

    public bool AcceptMoodSuggestion(string mood)
    {
        var offered = MoodSuggestions.FirstOrDefault(m => m.Equals(mood.Trim(), StringComparison.OrdinalIgnoreCase));
        if (offered is null)
            return false;

        MoodSuggestions = MoodSuggestions.Where(m => m != offered).ToList();
        if (Moods.Contains(offered, StringComparer.OrdinalIgnoreCase))
            return true;

        ValidationException.Must(Moods.Count < MetadataListMaxCount, SuggestionField, $"A Session must not have more than {MetadataListMaxCount} Moods.");
        Moods = [.. Moods, offered];
        MetadataVersion++;
        return true;
    }

    public bool RejectTopicSuggestion(string topic)
    {
        var kept = TopicSuggestions.Where(t => !t.Equals(topic.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        var changed = kept.Count != TopicSuggestions.Count;
        TopicSuggestions = changed ? kept : TopicSuggestions;
        return changed;
    }

    public bool RejectMoodSuggestion(string mood)
    {
        var kept = MoodSuggestions.Where(m => !m.Equals(mood.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        var changed = kept.Count != MoodSuggestions.Count;
        MoodSuggestions = changed ? kept : MoodSuggestions;
        return changed;
    }

    public PersonProposal? ProposalFor(string label)
        => PeopleProposals.FirstOrDefault(p => p.Label.Equals(label.Trim(), StringComparison.OrdinalIgnoreCase));

    public void DropPersonProposal(PersonProposal proposal)
        => PeopleProposals = PeopleProposals.Where(p => p != proposal).ToList();

    /// <summary>Approval adds the mention to Cleaned, and the words before it stay as a Revision. Raw does not change.</summary>
    public void ApprovePersonProposal(PersonProposal proposal, string personId, string personLabel, DateTimeOffset now)
    {
        var plain = ProseMirrorToPlainText.Render(Cleaned.Text);
        var cleaned = MentionInsertion.Insert(Cleaned.Text, PeopleTagSpans.Spans(plain, proposal.Label, personId, personLabel));
        if (cleaned != Cleaned.Text)
            ValidationException.ThrowWhenLongerThan(cleaned, DraftMaxLength, "label", "A tag of this name makes Cleaned too long. Choose Do not tag, or run a new Cleanup.");

        DropPersonProposal(proposal);
        if (cleaned != Cleaned.Text)
        {
            Cleaned = Cleaned.RewrittenKeepingSavedAt(cleaned);
            _appendedCleanedRevisions.Add(new CleanedRevision(Id, Cleaned.DraftRevision, now, now, cleaned));
        }

        RefreshFromDrafts();
    }

    private bool HoldsTopic(string topic)
        => UserTopics.Concat(AiTopics).Any(held => held.Trim().Equals(topic.Trim(), StringComparison.OrdinalIgnoreCase));

    private void RequireRunningCleanup()
        => ValidationException.Must(CleanupRun == CleanupRunState.Running, CleanupField, "No Cleanup of this Session runs.");

    private static DateTimeOffset StoredInstantOf(DateTimeOffset instant)
        => new(instant.UtcTicks - instant.UtcTicks % TimeSpan.TicksPerMicrosecond, TimeSpan.Zero);

    private static bool IsStale(int rawDraftRevision, int? lastCleanupRawRevision)
        => lastCleanupRawRevision is { } read && rawDraftRevision > read;

    private void RefreshFromDrafts()
    {
        Preview = PreviewOf(Raw.Text);

        var mentioned = PersonMentions.PersonIdsIn(Raw.Text).Union(PersonMentions.PersonIdsIn(Cleaned.Text)).Order(StringComparer.Ordinal);
        if (!mentioned.SequenceEqual(MentionedPersonIds))
            MentionedPersonIds = mentioned.ToList();
    }

    private static string PreviewOf(string draft)
    {
        var preview = new StringBuilder();
        foreach (var rune in ProseMirrorToPlainText.Render(draft).EnumerateRunes().Where(r => r.Value != 0).Take(PreviewMaxLength))
            preview.Append(rune.ToString());
        return preview.ToString();
    }

    protected Session() : base(IdPrefix) { }
}
