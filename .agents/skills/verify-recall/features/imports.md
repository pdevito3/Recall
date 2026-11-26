# Imports

An agent puts the entries of an old journal into an import batch over REST or MCP with a personal access token. The person reviews the batch at `/imports/<id>`: the entries show by day, in pages of whole days, each with its start time in its zone, its words, Moods, and Topics. The person edits the words or the time of an entry, rejects an entry or a whole day, and accepts the batch. The accept makes each kept entry a Session in the journal, and the page then shows the counts of the result. A discard drops the batch.

## Sub-features

- `list` at `/imports` shows `Your import batches`: each name links to its batch, with `Open` or `In your journal`, `<n entries> in <zone>`, and `Opened on ...`. With no batch, the text names the personal access token.
- `discard` opens `Discard <name>?` from `Discard <name>` and drops the batch with `Discard batch` and the toast `Batch discarded`. An accepted batch has no Discard.
- `days` show as `h2` headings with the list `Entries of <day>`. Each entry is the list item `Entry at <time>`. A batch of more than 200 entries has the pager `Pages of this batch`, and the URL has `?page=<n>`.
- `entry` shows its time, the zone when it is not the zone of the batch, `Time from its place in the day` when no one gave a time, `Edited`, `Rejected` or `Rejected with the day`, and the notes `The import changed the words after your edit. ...` and `The import changed the time after your edit. ...`.
- `edit` opens the form `Edit the entry` from `Edit the entry at <time>`: `Words`, `Time`, `Save`, `Cancel`, and after an edit `Use the import words` and `Use the import time`. A refusal shows in the form.
- `reject` sends `Reject the entry at <time>` or `Keep the entry at <time>`. `Reject <day>` and `Keep <day>` do the same for a day.
- `counts` in `Counts of this batch` show `<n entries>: <k> kept, <r> rejected` and change after each review.
- `accept` opens `Accept this batch?` with the kept and rejected counts. `Accept batch` shows `Result of the accept` and `Go to your journal`. A 409 keeps the dialog open with the message and the new counts.
- `refusals`: a 404 or a 409 of a review shows the message of the Server in a toast, reads the batch again, and moves the focus to the heading when the entry leaves. A batch that does not exist says `This import batch does not exist.` with `Go to your imports`.
- `setting` on `/settings`, section `Imports`: the checkbox `Only I can accept an import batch`.

## How to get to it (user POV)

- Choose `Imports` in the sidebar, or open `/imports`. Choose the name of a batch.
- Open `/settings` for the setting.

## Driving it with browse.mjs

Preconditions:

- `doctor.sh` exits with 0.
- The database has no tenant, so `login()` makes an Admin with no batch.

- **Token.** On `/settings/personal-access-tokens`, make a token with `Change my journal`. The agent calls use it as `Authorization: Bearer <value>`.
- **Agent.** `POST /api/v1/import-batches` with a name and `America/New_York`, then `PUT /api/v1/import-batches/<id>/entries` with entries on several days: with a time, without a time, with its own zone, with Moods and Topics.
- **List.** Open `/imports`. The row of the batch shows the count and the zone of the API answer.
- **Review.** Choose the batch. Each entry shows the start time in its zone. Edit words and time, `Save`, and the `PUT .../entries/review` body has the whole review.
- **Reject.** `Reject the entry at <time>` and `Reject <day>`. The counts change.
- **Gone.** Remove an entry with `POST .../entries/remove`, then choose its Reject: the 404 message shows, the entry leaves, and the focus is on the `h1`.
- **Accept.** `Accept`, then put one more entry as the agent, then `Accept batch`: the 409 message and the new counts show. `Accept batch` again shows the result. `GET /api/v1/sessions` has the start times of the review.
- **Setting and discard.** Toggle the setting off and on. Open a second batch and discard it from the list.

Recipe: `recipes/imports.mjs`.

## Gotchas

- The sweep gives each batch name a long value, so the recipe reads the name from the list answer, not from a literal.
- The text of the `Time` field has Unicode isolate marks, so match it with a pattern such as `/7:45\W*AM/`.
- A fetch from the page needs `X-CSRF: 1` and `X-Recall-User: <sub of /auth/me>`. A token needs `neverExpires: true` or a future `expiresAt`.
