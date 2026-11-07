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
| 8 | `PUT api/sessions/{id}/draft` | `PUT /api/v1/sessions/{id}/draft` | Send `text`, `baseRevision`, and `savedAt`. `baseRevision` is required. Send 0 when no Revision exists. The reply has `outcome`, which is `Won`, `Lost`, or `Unchanged`. See [the base of a Draft save](#the-base-of-a-draft-save). Fixtures: `draft-save-won`, `draft-save-lost`. |
| 9 | `GET api/summaries/{period}/{date}` | R10 builds it | No route in this unit. |
| 10 | `POST api/summaries/{period}/{date}/generate` | R10 builds it | No route in this unit. |
| 11 | `GET api/me/settings` | `GET /api/v1/me/settings` | `timeZone`, not `timeZoneId`, and `requirePeopleProposalApproval`, not `requirePeopleTagApproval`. `version` is the base of the next settings save, and it is 0 when the User has no row. `timeZoneIsKnown` is false when the host does not know the stored zone, for example after a restore onto other tzdata. The Journaling day then comes from UTC. Show the zone and ask the person for a new one. Fixture: `user-settings`. |
| 12 | `PUT api/me/settings` | `PUT /api/v1/me/settings` | Send `locationCaptureEnabled`, `requirePeopleProposalApproval`, and `version`. All three are required, and a body without one of them gives 400. `timeZone` is optional, and the PUT replaces the whole row, so a body with no `timeZone`, with `null`, or with an empty or blank string stores no zone. The reply then has `timeZone: null`, and the Journaling day comes from UTC. A zone that the host does not know gives 422, except the stored zone and a change that does nothing. A stale `version` gives 409. Do not send `savedAt`, because the server ignores it. The POC `UserSettingsStore.execute` starts a PUT for each edit and does not wait for the one before it, so R14 must change it. See [Settings and metadata saves](#settings-and-metadata-saves). Fixtures: `user-settings`, `error-stale-settings`. |
| 13 | `GET api/sync/changes` | `GET /api/v1/sync/changes` | Send `cursor`, not `since`, and `pageSize`. The reply adds `hasMore` and `tombstones`. Pull again with no wait while `hasMore` is true. Remove the local row of each tombstone. A Session has `metadataVersion` and the settings row has `version`, so keep each with the values of its row as the base of the next save. A 422 has one meaning, with the key `cursor` in `errors`: the cursor is ahead of the server, because the server was restored. Drop the cursor and the synced rows, keep the unsent saves, and pull from the start. A restore moves the versions back, so an old base can match again. Build each unsent settings or metadata save again from the new row, as in step 4 of [Settings and metadata saves](#settings-and-metadata-saves). The 422 comes only while the cursor is ahead of the newest change. After new saves move the feed to that cursor or past it, the client has no sign of the restore, and an old base can match with a 200, because the server has no restore marker. A bad parameter gives 400, and a stored zone that the host does not know gives 200. Fixtures: `sync-changes`, `sync-changes-with-tombstone`, `error-cursor-ahead`. |
| 14 | `POST api/sessions/{id}/suggestions/accept` and `.../reject` | R9 builds it | No route in this unit. The call site is one for two routes. |
| 15 | `POST api/sessions/{id}/people-proposals/respond` | R9 builds it | No route in this unit. |
| 16 | `POST api/sessions/{id}/cleanup/stream` | R9 builds it | No route in this unit. |
| 17 | `POST api/sessions/{id}/cleanup` | R9 builds it | No route in this unit. |
| 18 | `PUT api/sessions/{id}/cleaned` | `PUT /api/v1/sessions/{id}/cleaned` | Send `text`, `baseRevision`, and `savedAt`, not `cleanedText` and `clientSavedAt`. Fixture: `cleaned-save`. |
| 19 | `POST api/sessions/{id}/cleanup/result` | R9 builds it | No route in this unit. |
| 20 | `PUT api/sessions/{id}/metadata` | `PUT /api/v1/sessions/{id}/metadata` | Send `userTopics`, not `topics`, and `metadataVersion`. Only `metadataVersion` is required, and a body without it gives 400. It is not the `version` of the Session, which moves on each Draft save. The PUT replaces Activity, Moods, and User Topics, and AI Topics stay. So a body with only `userTopics` and `metadataVersion` sets Activity to `None` and Moods to empty, and a body with only `metadataVersion`, or with `topics` in place of `userTopics`, also empties the User Topics. Moods and Topics return trimmed and in one spelling. Build `userTopics` only from the Topics with the provenance `UserSet`, because `topics` also holds AI Topics, and a save of an AI Topic in `userTopics` makes it a Topic of the User. A stale `metadataVersion` gives 409, and the words of the Session stay. Do not send `savedAt`, because the server ignores it. See [Settings and metadata saves](#settings-and-metadata-saves). The People of a Session come from its mentions, so the body has no list of people. Fixtures: `session-metadata`, `error-stale-metadata`. |
| 21 | `GET api/sessions/{id}/revisions` and `.../cleaned-revisions` | `GET /api/v1/sessions/{id}/raw-revisions` and `.../cleaned-revisions` | The body is `{ items, nextCursor }`, newest first. An item has `number` and `savedAt`, not `revisionNumber` and `createdAt`, and adds `length`. Fixtures: `raw-revision-page`, `cleaned-revision-page`. |
| 22 | `GET api/sessions/{id}/{revisions path}/{number}` | `GET /api/v1/sessions/{id}/raw-revisions/{number}` and `.../cleaned-revisions/{number}` | `text`, not `content`. Fixtures: `raw-revision`, `cleaned-revision`. |

## The base of a Draft save

The rules are SES-5, SES-6, SES-8, and SES-36 in the [sessions spec](specs/sessions.md). They apply to the `draft` route and to the `cleaned` route.

`baseRevision` is the number of a Draft whose text the client holds. Get this number from one of these sources:

- A read of the Session: `rawDraftRevision` with `rawText`, or `cleanedDraftRevision` with `cleanedText`.
- A `Won` reply or an `Unchanged` reply: `draftRevision`.

Do not use `latestRevision` as a base. It can name a Revision that lost.

Each `outcome` tells the client this:

| `outcome` | The Draft | The words of this save | `draftRevision` |
|-----------|-----------|------------------------|-----------------|
| `Won` | Holds these words | In the Draft, and in a new Revision | The number of the Draft |
| `Unchanged` | Holds these words | In the Draft. No new Revision | The number of the Draft |
| `Kept` | Took these words at an earlier save. It can hold more words now, or other words | In an earlier Revision. No new Revision | The number at which the Draft took these words |
| `Lost` | Never took these words in this save | In a Revision | `null` |

A replay of a save that won gives `Kept`, and a replay of a save that lost gives `Lost`. Neither makes a new Revision. So a client that lost a reply gets the true answer when it sends the save again. After `Kept`, do not merge these words into the Draft again, because the Draft already took them. Read the Session to see what changed. A Revision from before the `was_draft` column reads as a loss, so a replay of an older win can still give `Lost`.

A base above the number of the Draft gives `Lost`, also with a later `savedAt`. The server keeps the words as the newest Revision, and it does not refuse the save with a 4xx.

After `Lost`, do these steps:

1. Read the Session.
2. Merge the words of the client into the text of the read.
3. Save the merge, with the number of the read as `baseRevision`.
4. If the reply is `Lost` again, go back to step 1.

Do not send a new save from the old base and expect it to win. While a transcript moves the Draft, a save from a base older than the last chunk loses at any `savedAt`.

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

An older client sends `savedAt` and no version. The POC client, and any client of the earlier body, gets 400 on these two routes, with the missing field in the reply. Nothing changes on the server, so the older client overwrites no value. It cannot change settings or metadata until it sends a version. Its Draft saves work as before, so it loses no words.

## Fields that this unit does not add

`SessionDto` has no `suggestions` and no `peopleProposals`. R9 owns rows 14, 15, and 19, and it adds these fields and their routes. Until then, the client must read them as absent.

## Journaling day

A change of the time zone moves a Session to a new day, but the sync feed does not send the Session again. The feed sends the settings row. Derive the day from `startedAt` and the zone of the settings row, or from UTC when `timeZone` is null or `timeZoneIsKnown` is false. Do not keep `journalingDay` from an older response.

## New routes

- `DELETE /api/v1/sessions/{id}` removes a Session. The feed then has a tombstone. See [ADR 0020](adr/0020-journal-rules-in-the-database.md).
- `POST`, `GET /api/v1/people`, and `GET`, `PUT /api/v1/people/{id}`. A rename changes each mention. Fixtures: `person`, `people`.
- `POST`, `GET /api/v1/corrections`, and `GET`, `PUT`, `DELETE /api/v1/corrections/{id}`. Fixtures: `correction`, `corrections`.
