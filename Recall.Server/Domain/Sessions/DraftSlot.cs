namespace Recall.Server.Domain.Sessions;

public sealed record DraftSlot(string Text, int DraftRevision, int LatestRevision, DateTimeOffset? SavedAt)
{
    public const int NoRevision = 0;

    public static readonly DraftSlot Empty = new(string.Empty, NoRevision, NoRevision, null);

    public bool NeedsReplayLookup(DraftSave save) => save.Text != Text && save.BaseRevision != DraftRevision;

    public DraftDecision Decide(DraftSave save, bool isReplay, bool missesTranscript = false)
    {
        if (save.Text == Text)
            return new DraftDecision(this, DraftSaveOutcome.Unchanged, null);

        if (isReplay)
            return new DraftDecision(this, DraftSaveOutcome.Unchanged, null);

        var number = LatestRevision + 1;
        return !missesTranscript && Beats(save)
            ? new DraftDecision(new DraftSlot(save.Text, number, number, LaterOf(SavedAt, save.SavedAt)), DraftSaveOutcome.Won, number)
            : new DraftDecision(this with { LatestRevision = number }, DraftSaveOutcome.Lost, number);
    }

    public DraftSlot RewrittenKeepingSavedAt(string text) => new(text, LatestRevision + 1, LatestRevision + 1, SavedAt);

    private bool Beats(DraftSave save)
        => save.BaseRevision == DraftRevision || SavedAt is not { } draftSavedAt || save.SavedAt >= draftSavedAt;

    private static DateTimeOffset LaterOf(DateTimeOffset? first, DateTimeOffset second)
        => first is { } value && value > second ? value : second;
}

public readonly record struct DraftDecision(DraftSlot Next, DraftSaveOutcome Outcome, int? MintedRevision);
