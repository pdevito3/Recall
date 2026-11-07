namespace Recall.Server.Domain.Sessions;

public sealed record DraftSlot(string Text, int DraftRevision, int LatestRevision, DateTimeOffset? SavedAt)
{
    public const int NoRevision = 0;

    public static readonly DraftSlot Empty = new(string.Empty, NoRevision, NoRevision, null);

    public DraftDecision Decide(DraftSave save, DraftAnswer? earlier = null, bool baseCanWin = true)
    {
        if (save.Text == Text)
            return new DraftDecision(this, DraftSaveOutcome.Unchanged, null, DraftRevision);

        if (earlier is { DraftTookAt: var number })
            return new DraftDecision(this, number is null ? DraftSaveOutcome.Lost : DraftSaveOutcome.Kept, null, number);

        var next = LatestRevision + 1;
        return baseCanWin && Beats(save)
            ? new DraftDecision(new DraftSlot(save.Text, next, next, LaterOf(SavedAt, save.SavedAt)), DraftSaveOutcome.Won, next, next)
            : new DraftDecision(this with { LatestRevision = next }, DraftSaveOutcome.Lost, next, null);
    }

    public DraftSlot RewrittenKeepingSavedAt(string text) => new(text, LatestRevision + 1, LatestRevision + 1, SavedAt);

    private bool Beats(DraftSave save)
        => save.BaseRevision == DraftRevision
           || (save.BaseRevision < DraftRevision && (SavedAt is not { } draftSavedAt || save.SavedAt >= draftSavedAt));

    private static DateTimeOffset LaterOf(DateTimeOffset? first, DateTimeOffset second)
        => first is { } value && value > second ? value : second;
}

/// <summary>The first Revision that already answers a save. <paramref name="DraftTookAt"/> is the number at which the Draft took the words, and null when this save lost. SES-8.</summary>
public readonly record struct DraftAnswer(int? DraftTookAt);

/// <summary>What a save does to a Draft. <c>NextBase</c> is the number of a Draft whose words the client holds, or null when it holds none. SES-36.</summary>
public readonly record struct DraftDecision(DraftSlot Next, DraftSaveOutcome Outcome, int? MintedRevision, int? NextBase);
