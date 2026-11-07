# iOS API changes

This document compares each of the 22 routes that the POC iOS client calls (`JournalRecallAPIClient.swift`, `TokenManager.swift`) with the Recall API. R14 uses it. Each new route is under `/api/v1`. The real responses are in [`tests/Recall.ContractTests/fixtures/`](../tests/Recall.ContractTests/fixtures/).

## Changes on each route

- An ID is a text such as `ses_0199f0c1a2b37c4d8e9f0a1b2c3d4e5f`, not a UUID. Change `UUID` to `String` for each ID that the server makes.
- A time on a Draft save is `savedAt`, not `clientSavedAt`. Send each time with its offset, such as `2026-06-01T08:00:00.000Z`. A time with no offset gives 400. The settings and metadata saves take no time. They take a base version. See [Settings and metadata saves](#settings-and-metadata-saves).
- An error is a problem document (`status`, `title`, `detail`, `errors`). See `error-*.json`.

## Routes

| # | POC route | Recall route | Change |
| --- | --- | --- | --- |
| 1 | `POST api/auth/login` | R3 owns it | Recall has app-owned identity. See `docs/authentication.md`. This unit does not change it. |
| 2 | `GET api/me` | `GET /api/v1/users/me` | R3 owns it. |
| 3 | `POST api/auth/logout` | R3 owns it | See `docs/authentication.md`. |
| 4 | `POST api/auth/refresh` | R3 owns it | See `docs/authentication.md`. |
| 5 | `POST api/sessions` | `POST /api/v1/sessions` | Send `clientSessionId` in place of `id`, and add `startedAt`. The server makes `id`. A replay gives 200 with the same Session. A replay after a delete gives 409, so drop the local copy. Fixtures: `session-created`, `session-replayed`, `error-replay-after-delete`. |
| 6 | `GET api/sessions` | `GET /api/v1/sessions` | The body is `{ items, nextCursor }`, not a list. Send `cursor` and `pageSize`. A row has `startedAt`, not `createdAt`, and `personIds`, not `people`. `topics` holds `{ name, provenance }`. A row adds `metadataVersion`, the base of the next metadata save. Fixtures: `session-page`, `session-page-with-next-cursor`. |
| 7 | `GET api/sessions/{id}` | `GET /api/v1/sessions/{id}` | Fields as in `session`, also `metadataVersion`, the base of the next metadata save. Another person's Session gives 404. |
| 8 | `PUT api/sessions/{id}/draft` | `PUT /api/v1/sessions/{id}/draft` | Send `text`, `baseRevision`, and `savedAt`. `baseRevision` is required. Send 0 when no Revision exists. The reply has `outcome`, which is `Won`, `Unchanged`, `Kept`, or `Lost`, and `Kept` and `Lost` have their own fixtures, `draft-save-kept` and `draft-save-lost`. See [the base of a Draft save](#the-base-of-a-draft-save). Fixtures: `draft-save-won`, `draft-save-kept`, `draft-save-lost`. A document with a link to `javascript:`, `vbscript:`, `data:`, `blob:`, `filesystem:`, or `file:` gives 422, and the Draft stays as it was. Any other scheme saves. |
| 9 | `GET api/summaries/{period}/{date}` | R10 builds it | No route in this unit. |
| 10 | `POST api/summaries/{period}/{date}/generate` | R10 builds it | No route in this unit. |
| 11 | `GET api/me/settings` | `GET /api/v1/me/settings` | `timeZone`, not `timeZoneId`, and `requirePeopleProposalApproval`, not `requirePeopleTagApproval`. `version` is the base of the next settings save, and it is 0 when the User has no row. `timeZoneIsKnown` is false when the host does not know the stored zone, for example after a restore onto other tzdata. The Journaling day then comes from UTC. Show the zone and ask the person for a new one. Fixture: `user-settings`. |
| 12 | `PUT api/me/settings` | `PUT /api/v1/me/settings` | Send `locationCaptureEnabled`, `requirePeopleProposalApproval`, and `version`. All three are required, and a body without one of them gives 400. `timeZone` is optional, and the PUT replaces the whole row, so a body with no `timeZone`, with `null`, or with an empty or blank string stores no zone. The reply then has `timeZone: null`, and the Journaling day comes from UTC. A zone that the host does not know gives 422, except the stored zone and a change that does nothing. A stale `version` gives 409. Do not send `savedAt`, because the server ignores it. The POC `UserSettingsStore.execute` starts a PUT for each edit and does not wait for the one before it, so R14 must change it. See [Settings and metadata saves](#settings-and-metadata-saves). Fixtures: `user-settings`, `error-stale-settings`. |
| 13 | `GET api/sync/changes` | `GET /api/v1/sync/changes` | Send `cursor`, not `since`, and `pageSize`. The reply adds `hasMore` and `tombstones`. Pull again with no wait while `hasMore` is true. Remove the local row of each tombstone. A Session has `metadataVersion` and the settings row has `version`, so keep each with the values of its row as the base of the next save. A 422 has one meaning, with the key `cursor` in `errors`: the cursor is ahead of the server, because the server was restored. Drop the cursor and the synced rows, keep the unsent saves, and pull from the start. A restore moves the versions back, so an old base can match again. Build each unsent settings or metadata save again from the new row, as in step 4 of [Settings and metadata saves](#settings-and-metadata-saves). The 422 comes only while the cursor is ahead of the newest change. After new saves move the feed to that cursor or past it, the client has no sign of the restore, and an old base can match with a 200, because the server has no restore marker. A bad parameter gives 400, and a stored zone that the host does not know gives 200. Fixtures: `sync-changes`, `sync-changes-with-tombstone`, `error-cursor-ahead`. |
| 14 | `POST api/sessions/{id}/suggestions/accept` and `.../reject` | `POST /api/v1/sessions/{id}/suggestions/accept` and `.../reject` | Send `kind` (`Topic` or `Mood`) and `value`. The reply is the Session. A value that the Session no longer offers gives 200 and no change, so a retry is safe. The POC gave 404 there. An unknown `kind` gives 422. |
| 15 | `POST api/sessions/{id}/people-proposals/respond` | `POST /api/v1/sessions/{id}/people-proposals/respond` | Send `label` and `approve`, and optionally `bindToPersonId` or `createNew`. The reply is the Session. An approve adds each mention to Cleaned as a new Revision. An approve with neither `bindToPersonId` nor `createNew` uses the match of the proposal, then the Person of that name, and adds one only if there is none. An approve that makes Cleaned too long gives 422. An approve of a name that a mark crosses, as in `**Sam** Lee`, or of a name that Cleaned no longer holds, gives 422 on `label` and adds no Person. The proposal stays, so show the message and offer `approve: false`. `isNew` says that there was no match when the Cleanup ran, so it can be out of date. Send `createNew` only when the person asks for a second Person of that name. `approve: false` drops the proposal. Raw does not change. |
| 16 | `POST api/sessions/{id}/cleanup/stream` | `POST /api/v1/sessions/{id}/cleanup/stream` | Server-sent events. Each event is `{ type, data: { message, stopReason } }`, and `type` is `run.started`, `progress`, `completed`, `failed`, or `stopped`. The stream shows progress of the job. A closed stream leaves the job running. No event holds the words: read the Session after `completed`. iOS `CleanupEvent.swift` also reads `data.text` and `model.delta`. The server never sends them. A POST after a finished run starts a new run. `engine` in the body is ignored. |
| 17 | `POST api/sessions/{id}/cleanup` | `POST /api/v1/sessions/{id}/cleanup` | No body is needed, and `engine` is ignored. The reply is 202 with the Session in `Running`. A job does the work, so the client can leave. A second POST while a run runs joins that run, and gives 202. A Session with no words, or an install with no active model provider that has a model, gives 422. A key that the server cannot read gives 202, and the run then fails with `KeyUnreadable`. |
| 17a | none, this route is new | `POST /api/v1/sessions/{id}/cleanup/stop` | No body. It stops a running Cleanup and gives 200 with the Session. The Session then has the status `Failed`, or `Stale` when Raw is newer than Cleaned, and the code `Stopped`. A Session with no running Cleanup stays as it is and gives 200. |
| 18 | `PUT api/sessions/{id}/cleaned` | `PUT /api/v1/sessions/{id}/cleaned` | Send `text`, `baseRevision`, and `savedAt`, not `cleanedText` and `clientSavedAt`. Fixture: `cleaned-save`. The refusal of a link in row 8 applies. |
| 19 | `POST api/sessions/{id}/cleanup/result` | `POST /api/v1/sessions/{id}/cleanup/result` | The result of the `OnDevice` engine. Send `baseRawRevision`, `baseCleanedRevision` (the `cleanedDraftRevision` that the device read), and `cleanedMarkdown`, and optionally `synopsis`, `topicSuggestions`, `moodSuggestions`, and `peopleProposal`. It stores a Revision as a job does. A result applies once. The same result again, with the same base and each field the same, gives 200 with the stored Session and writes nothing, so a later hand edit, tag, or Cleanup stays. A device that runs a Cleanup again and gets the same words gets the same answer. The check for the same result comes first, so the same result while a job runs also gives 200, with the Session as it is. Another result while a job runs, or from a Revision that does not exist, gives 422. A `baseRawRevision` below 1 gives 400. If Cleaned moved after `baseCleanedRevision`, Cleaned stays, the result is a Revision, and `waitingCleanupRevision` of the Session names it. A `baseCleanedRevision` above the newest Revision gives 422. |
| 19a | none, this route is new | `POST /api/v1/sessions/{id}/cleanup/waiting/apply` | Body `{ revision, baseCleanedRevision }`. It puts the result that waits in Cleaned and gives 200 with the Session. A `revision` that does not wait, or a `baseCleanedRevision` that is not the current `cleanedDraftRevision`, gives 409, so read the Session again. |
| 20 | `PUT api/sessions/{id}/metadata` | `PUT /api/v1/sessions/{id}/metadata` | Send `userTopics`, not `topics`, and `metadataVersion`. Only `metadataVersion` is required, and a body without it gives 400. It is not the `version` of the Session, which moves on each Draft save. The PUT replaces Activity, Moods, and User Topics, and AI Topics stay. So a body with only `userTopics` and `metadataVersion` sets Activity to `None` and Moods to empty, and a body with only `metadataVersion`, or with `topics` in place of `userTopics`, also empties the User Topics. Moods and Topics return trimmed and in one spelling. Build `userTopics` only from the Topics with the provenance `UserSet`, because `topics` also holds AI Topics, and a save of an AI Topic in `userTopics` makes it a Topic of the User. A stale `metadataVersion` gives 409, and the words of the Session stay. Do not send `savedAt`, because the server ignores it. See [Settings and metadata saves](#settings-and-metadata-saves). The People of a Session come from its mentions, so the body has no list of people. Fixtures: `session-metadata`, `error-stale-metadata`. |
| 21 | `GET api/sessions/{id}/revisions` and `.../cleaned-revisions` | `GET /api/v1/sessions/{id}/raw-revisions` and `.../cleaned-revisions` | The body is `{ items, nextCursor }`, newest first. An item has `number` and `savedAt`, not `revisionNumber` and `createdAt`, and adds `length`. Fixtures: `raw-revision-page`, `cleaned-revision-page`. |
| 22 | `GET api/sessions/{id}/{revisions path}/{number}` | `GET /api/v1/sessions/{id}/raw-revisions/{number}` and `.../cleaned-revisions/{number}` | `text`, not `content`. Fixtures: `raw-revision`, `cleaned-revision`. |

## The base of a Draft save

The rules are SES-5, SES-6, SES-7, SES-8, and SES-36 in the [sessions spec](specs/sessions.md). They apply to the `draft` route and to the `cleaned` route.

A save wins only when `baseRevision` is the number of the current Draft. A save from any other number gets `Lost`. `savedAt` plays no part in this, so the clock of a device cannot win or lose a save.

`baseRevision` is the number of a Draft whose text the client holds. Send only a number from one of these sources.

- A read of the Session: `rawDraftRevision` with `rawText`, or `cleanedDraftRevision` with `cleanedText`.
- A `Won`, `Unchanged`, or `Kept` reply: `draftRevision`.

Do not use `latestRevision` as a base. It can name a Revision that lost, and that number never wins.

Keep one save in flight for each Draft, and send the next save from the number of the reply. A second save from the same base gets `Lost`. An offline queue holds the newest text for a Draft, and it does not send each older text.

Each `outcome` tells the client this:

| `outcome` | The Draft | The words of this save | `draftRevision` |
|-----------|-----------|------------------------|-----------------|
| `Won` | Holds these words | In the Draft, and in a new Revision | The number of the Draft |
| `Unchanged` | Holds these words | In the Draft. No new Revision | The number of the Draft |
| `Kept` | Took these words in the first try of this save. It can hold other words now | In an earlier Revision. No new Revision | The number at which the Draft took these words |
| `Lost` | Did not take this save | In a Revision | `null` |

A replay sends the same `text` and the same `savedAt`. Give each new save a new `savedAt`, to the millisecond or finer, and keep the `savedAt` of a queued save when you send it again. A replay of a save that won gives `Kept`, and a replay of a save that lost gives `Lost`. Neither makes a new Revision. So a client that lost a reply gets the true answer when it sends the save again. A replay of a save that gave `Unchanged` gives `Unchanged` while the Draft holds the words. After the Draft moved, it is a new save, so from its older base it gets `Lost`. After `Kept`, do not merge these words into the Draft again, because the Draft already took them. The Draft can hold other words now, so read the Session before you show its text.

Any write can answer 507 when the disk of the server is full. The save did not happen. Keep the words, and send the same save again later.

A base that is not the number of the current Draft gives `Lost`, also with a later `savedAt`. The server keeps the words as the newest Revision, and it does not refuse the save with a 4xx.

Two devices that edit one Draft see more `Lost` answers, and a chunk of a transcript, a Person rename, a Cleanup, or the approval of a Person also moves the number. Each `Lost` costs the client one merge, or one notice to the person. A `Lost` save does not change the Draft.

After `Lost`, do these steps:

1. Take the words out of the queue entry of the save, and keep them as the words to merge. Do not send the lost save again.
2. Read the Session.
3. Compare the words with the text of the read. If the text holds them, stop, and use the number of the read as the next base. A merge that lost its reply can be in the Draft.
4. Merge the words into the text of the read, one time.
5. Save the merge, with the number of the read as `baseRevision` and a new `savedAt`. The old `savedAt` with the same words is a replay, and it gives `Lost` again.
6. If the reply is `Lost` again, or the save has no reply, go back to step 2. A writer whose time from its read to its save is at least the save interval of another writer on the same Session gets `Lost` each time while the other writer keeps its pace. In a 12 s run, a writer with 300 ms from read to save won 0 of 38 saves against a writer that saved every 250 ms ([ADR 0021](adr/0021-a-draft-save-wins-only-from-the-current-draft.md) gives each run). So after 3 `Lost` answers in a row, wait before step 2. Wait 1 s first, then double the wait after each next `Lost`, up to 30 s, and add a random part of up to half the wait. A `Won`, `Unchanged`, or `Kept` reply ends the waits. While the client waits, show the person that the words wait to save. Do not say that the words are lost: each `Lost` save keeps them in a Revision, and the queue still holds them.

These steps stop a repeat of text. A merge from an old queue entry adds the words a second time when the merge before it is in the Draft.

Step 3 finds only words that a save added. A save that only took words out has nothing to find, so show a notice to the person, or make the same change on the text of the read.

Do not send a new save from the old base and expect it to win. It gets `Lost` at any `savedAt`.

## Settings and metadata saves

The rules are SES-31 to SES-35 in the [sessions spec](specs/sessions.md). The reasons are in [ADR 0019](adr/0019-last-write-wins-for-drafts.md).

The client must do these steps:

1. Keep one local copy of the settings row and of each Session. Replace the values and the version of a copy together, from a read, from the sync feed, or from a reply. Keep the copies and the queue for each account, and never send a queued change with the session of another account. The server cannot catch this when the versions are equal. The version is `version` on the settings row and `metadataVersion` on a Session.
2. Send the version of the copy in each save. On a fresh account with no settings row, send the 0 that the read gave.
3. Send the changes of a row one at a time and in order. Wait for the reply, and put it in the copy. Keep the fields that the person changed in the queue, not a whole body. Build each whole body from the copy and those fields, because the PUT replaces the row and a missing field clears its value. A queued whole body holds old values of the other fields, and with a current base the server takes them with a 200 and cannot know that they are old. If the queue holds two changes of one field, keep only the last one. Otherwise a queue of capture on and then capture off turns capture on for a time, and capture stays on if the app stops there.
4. On a 409, read the row again into the copy. For each queued change of that row, build the whole body from the new copy and the fields that the person changed, and send it with the new version. A field that the new copy already holds needs nothing. Do not send the stale body again with the new version, because that brings back the old value of each other field. Show the new value and ask the person before you apply a field in these cases:
   - The same field changed on the server.
   - The change turns location capture on.
   - The save had no reply before the 409, and the field differs from the new copy. The first try can have applied, and a newer change can have undone it.

   These cases apply only on a 409. A capture on that a person made while offline applies with a 200 and no prompt when the row did not change.

   Apply a change that turns location capture off with no prompt, also when the same field changed on the server, because capture stays on until it applies, and tell the person. This can undo a newer change on another device that turned capture on, and that direction is the safe one for privacy.

   A version covers a whole row and not one field, so a client cannot see a field that went to another value and back. A change of such a field applies with a 200 when no case above asks first.
5. On a failure with no reply, send the same body again with the same base. If the first try reached the server and finished, the reply is 200 with the stored row. If the first try of a settings save is still running, the reply can be 409, so go to step 4. If a newer change came after it, the reply is 409, so go to step 4. Two different bodies from one base cannot both change the row, so two overlapping saves do not lose a change.
6. Keep the 409 and the 400 out of the Draft queue. A refused metadata save does not stop a Draft save, so send the words on their own schedule.

The replies are these:

| Reply | Meaning |
| --- | --- |
| 200 | The row holds the values that you sent, after trimming. Take the base for the next save from this reply. |
| 400 | The body has no version. Nothing changed. |
| 409 with the type `urn:recall:problem:stale-version` | The row changed after the version that you sent. Nothing changed. `errors.version` or `errors.metadataVersion` holds the reason. |
| 409 with the type `urn:recall:problem:concurrent-change` | A metadata save lost the race on the row 20 times. Nothing changed. Send the same body again with the same base. |
| 422 | On a settings save, a zone that the host does not know. On a metadata save, a list of more than 50 values, a value of more than 100 characters, or a character that is not storable. Nothing changed. |

An older client sends `savedAt` and no version. The POC client, and any client of the earlier body, gets 400 on these two routes, with the missing field in the reply. Nothing changes on the server, so the older client overwrites no value. It cannot change settings or metadata until it sends a version. Its Draft saves follow the rule above, so a save that gets `Lost` keeps its words in a Revision.

## Fields of Cleanup

`SessionDto` adds `cleanupFailure`, `suggestions`, and `peopleProposals`. `cleanupFailure` is a closed code: `None`, `NoModelProvider`, `KeyUnreadable`, `ProviderRefused`, `ProviderUnavailable`, `BadModelOutput`, `Interrupted`, or `Stopped`. It never holds words of a model provider. `SessionSummaryDto`, the row of the Session list, adds `cleanupFailure` too, so a list can show `Stopped` and not `Failed` for a stop that the person asked for. A client that does not read these fields keeps working.

## Journaling day

A change of the time zone moves a Session to a new day, but the sync feed does not send the Session again. The feed sends the settings row. Derive the day from `startedAt` and the zone of the settings row, or from UTC when `timeZone` is null or `timeZoneIsKnown` is false. Do not keep `journalingDay` from an older response.

## New routes

- `DELETE /api/v1/sessions/{id}` removes a Session. The feed then has a tombstone. See [ADR 0020](adr/0020-journal-rules-in-the-database.md).
- `POST`, `GET /api/v1/people`, and `GET`, `PUT /api/v1/people/{id}`. A rename changes each mention. Fixtures: `person`, `people`.
- `POST`, `GET /api/v1/corrections`, and `GET`, `PUT`, `DELETE /api/v1/corrections/{id}`. Fixtures: `correction`, `corrections`.
