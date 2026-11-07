# A Draft save wins only from the current Draft

[ADR 0019](0019-last-write-wins-for-drafts.md) let a save from an older base win when its save time was not before the save time of the Draft. Three verifiers found one result of this rule: a word leaves the Draft and each writer gets `Won`. A device clock 10 s or more ahead did this with one person and two tabs, in 10 of 10 runs (R12 verify4, F1). Two writers with a client that follows the docs lost 6 to 20 of 50 words from the Draft (R11c verify3). A clock cannot tell which of two copies holds the newer words, because a person can hold a copy for any time.

So the clock no longer decides a save. The rules are in the [sessions spec](../specs/sessions.md), SES-5, SES-7, SES-8, and SES-36. For one save, in this order:

1. A replay, which is the same words with the same `savedAt`, gets the answer of its first try. A win gives `Kept`, a loss gives `Lost`, and neither makes a Revision.
2. Words equal to the Draft give `Unchanged`.
3. A base equal to the number of the current Draft gives `Won` with a new Revision.
4. Any other base gives `Lost`, with a new Revision and a null `draftRevision`.

A chunk, a rename, and a Cleanup each move the number, so rule 4 covers each of them. The separate guard of a chunk and its column are gone. The cap of a future time, the column of the time that the client claimed, and the rule that a win never moves the time back are also gone, because only the time rule needed them. `was_draft` stays, because a replay reads it to give `Kept` or `Lost`.

## Considered options

- **Keep the time rule and cap the skew.** A slow save of an older text still wins inside the cap, with no signal, so this makes the failure rarer and leaves it silent.
- **Give a 409 on a stale base.** A refusal tells a person that the words they spoke did not save. `Lost` keeps the words in a Revision and gives the same signal with a 200, as ADR 0019 states.

## Consequences

- Two writers see more `Lost` answers. Each client merges or shows a notice. So a client keeps one save in flight for each Draft, and an offline queue holds only the newest text. The steps are in [iOS API changes](../ios-api-changes.md#the-base-of-a-draft-save).
- A save from an older base that reached the server before this rule could win with no read. A client now reads first, each time that the Draft moved.
- A slow writer can lose each save while a fast writer keeps its pace. This is a limit, and the server does not fix it. A writer whose time from its read to its save is at least the save interval of another writer on the same Session gets `Lost` each time, and its words stay in Revisions. In 12 s runs with one fast writer A and one slow writer B on one Draft (the verifier of PR 69, `starve.json`), B won 0 of 38 saves with a 300 ms gap against A at a 250 ms pace, 1 of 74 with a 150 ms gap against a 100 ms pace, 0 of 107 with a 100 ms gap against a 50 ms pace, and 0 of 10 with a 1200 ms gap against a 1000 ms pace. With a gap shorter than the pace, B won 65 of 105 saves at a 100 ms gap against a 250 ms pace, and 27 of 38 at a 300 ms gap against a 1000 ms pace. The client rule is step 6 of [the steps after `Lost`](../ios-api-changes.md#the-base-of-a-draft-save): after a few `Lost` answers in a row, wait with a growing delay before the next read, and tell the person that the words wait to save.
- `savedAt` is what the client sent. A fast clock shows a time in the future on a Revision, and a win from a slow clock moves the time of the Draft back. A rename now sets the time of the Draft to the time of the server. Nothing on the server reads these times to make a decision.
- `Unchanged` writes no Revision, so its replay has no first answer to find after the Draft moves. SES-5 decides it, so from its older base it gets `Lost` with one new Revision. The client reads the Draft, and the words stay in a Revision.
- A client that guesses a number above the latest number can win later, when a Draft takes that number. SES-36 forbids this guess, and the server does not hold enough to refuse it at no cost. A restore of a backup moves the numbers back, so a client with a number from after the backup can do the same.
- The operator can reverse this decision. The change is one pull request with a clean revert, and the migration has a `Down`. `Down` cannot bring back two values: each older Revision shows the time of the client as its claimed time and 1970 as its capped time, and each Session has no last chunk until its next chunk.
