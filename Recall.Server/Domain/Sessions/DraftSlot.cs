namespace Recall.Server.Domain.Sessions;

public sealed record DraftSlot(string Text, int DraftRevision, int LatestRevision, DateTimeOffset? SavedAt)
{
    public const int NoRevision = 0;

    public static readonly DraftSlot Empty = new(string.Empty, NoRevision, NoRevision, null);

    public bool NeedsReplayLookup(DraftSave save) => save.Text != Text && save.BaseRevision != DraftRevision;

    public DraftDecision Decide(DraftSave save, DraftReplay? replay = null, bool missesTranscript = false)
    {
        if (save.Text == Text)
            return new DraftDecision(this, DraftSaveOutcome.Unchanged, null, DraftRevision);

        if (replay is { WonRevision: { } won })
            return new DraftDecision(this, DraftSaveOutcome.Kept, null, won);

        if (replay is not null)
            return new DraftDecision(this, DraftSaveOutcome.Lost, null, null);

        var number = LatestRevision + 1;
        return !missesTranscript && Beats(save)
            ? new DraftDecision(new DraftSlot(save.Text, number, number, LaterOf(SavedAt, save.SavedAt)), DraftSaveOutcome.Won, number, number)
            : new DraftDecision(this with { LatestRevision = number }, DraftSaveOutcome.Lost, number, null);
    }

    public DraftSlot RewrittenKeepingSavedAt(string text) => new(text, LatestRevision + 1, LatestRevision + 1, SavedAt);

    private bool Beats(DraftSave save)
        => save.BaseRevision == DraftRevision
           || (save.BaseRevision < DraftRevision && (SavedAt is not { } draftSavedAt || save.SavedAt >= draftSavedAt));

    private static DateTimeOffset LaterOf(DateTimeOffset? first, DateTimeOffset second)
        => first is { } value && value > second ? value : second;
}

/// <summary>An earlier try of a save, from its Revision. <paramref name="WonRevision"/> is the number at which the Draft took the words, and null when the try lost.</summary>
public readonly record struct DraftReplay(int? WonRevision);

/// <summary>What a save does to a Draft. <c>NextBase</c> is the number of a Draft whose words the client holds, or null when it holds none. SES-36.</summary>
public readonly record struct DraftDecision(DraftSlot Next, DraftSaveOutcome Outcome, int? MintedRevision, int? NextBase);
