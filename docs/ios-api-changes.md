# iOS API changes

This document compares each of the 22 routes that the POC iOS client calls (`JournalRecallAPIClient.swift`, `TokenManager.swift`) with the Recall API. R14 uses it. Each new route is under `/api/v1`. The real responses are in [`tests/Recall.ContractTests/fixtures/`](../tests/Recall.ContractTests/fixtures/).

## Changes on each route

- An ID is a text such as `ses_0199f0c1a2b37c4d8e9f0a1b2c3d4e5f`, not a UUID. Change `UUID` to `String` for each ID that the server makes.
- A time is `savedAt`, not `clientSavedAt`. Send each time with its offset, such as `2026-06-01T08:00:00.000Z`. A time with no offset gives 400.
- An error is a problem document (`status`, `title`, `detail`, `errors`). See `error-*.json`.

## Routes

| # | POC route | Recall route | Change |
| --- | --- | --- | --- |
| 1 | `POST api/auth/login` | R3 owns it | Recall has app-owned identity. See `docs/authentication.md`. This unit does not change it. |
| 2 | `GET api/me` | `GET /api/v1/users/me` | R3 owns it. |
| 3 | `POST api/auth/logout` | R3 owns it | See `docs/authentication.md`. |
| 4 | `POST api/auth/refresh` | R3 owns it | See `docs/authentication.md`. |
| 5 | `POST api/sessions` | `POST /api/v1/sessions` | Send `clientSessionId` in place of `id`, and add `startedAt`. The server makes `id`. A replay gives 200 with the same Session. A replay after a delete gives 409, so drop the local copy. Fixtures: `session-created`, `session-replayed`, `error-replay-after-delete`. |
| 6 | `GET api/sessions` | `GET /api/v1/sessions` | The body is `{ items, nextCursor }`, not a list. Send `cursor` and `pageSize`. A row has `startedAt`, not `createdAt`, and `personIds`, not `people`. `topics` holds `{ name, provenance }`. Fixtures: `session-page`, `session-page-with-next-cursor`. |
| 7 | `GET api/sessions/{id}` | `GET /api/v1/sessions/{id}` | Fields as in `session`. Another person's Session gives 404. |
| 8 | `PUT api/sessions/{id}/draft` | `PUT /api/v1/sessions/{id}/draft` | Send `text`, `baseRevision`, and `savedAt`. `baseRevision` is required. Send 0 when no Revision exists. The reply has `outcome`, which is `Won`, `Lost`, or `Unchanged`. Fixtures: `draft-save-won`, `draft-save-lost`. |
| 9 | `GET api/summaries/{period}/{date}` | R10 builds it | No route in this unit. |
| 10 | `POST api/summaries/{period}/{date}/generate` | R10 builds it | No route in this unit. |
| 11 | `GET api/me/settings` | `GET /api/v1/me/settings` | `timeZone`, not `timeZoneId`, and `requirePeopleProposalApproval`, not `requirePeopleTagApproval`. `timeZoneIsKnown` is false when the host does not know the stored zone, for example after a restore onto other tzdata. The Journaling day then comes from UTC. Show the zone and ask the person for a new one. Fixture: `user-settings`. |
| 12 | `PUT api/me/settings` | `PUT /api/v1/me/settings` | Send `locationCaptureEnabled`, `requirePeopleProposalApproval`, and `savedAt`. All three are required, and a body without one of them gives 400. `timeZone` is optional, and the PUT replaces the whole row, so a body with no `timeZone` sets the zone to UTC. A zone that the host does not know gives 422, except the stored zone. A `savedAt` that is not newer than the stored one changes nothing, and the reply has the stored settings. A `savedAt` more than 5 seconds ahead of the server and below a time that the row keeps also changes nothing. So after a clock far ahead, correct the device clock to get a change saved. |
| 13 | `GET api/sync/changes` | `GET /api/v1/sync/changes` | Send `cursor`, not `since`, and `pageSize`. The reply adds `hasMore` and `tombstones`. Pull again with no wait while `hasMore` is true. Remove the local row of each tombstone. A 422 has one meaning, with the key `cursor` in `errors`: the cursor is ahead of the server, because the server was restored. Drop the cursor and the synced rows, keep the unsent saves, and pull from the start. A bad parameter gives 400, and a stored zone that the host does not know gives 200. Fixtures: `sync-changes`, `sync-changes-with-tombstone`, `error-cursor-ahead`. |
| 14 | `POST api/sessions/{id}/suggestions/accept` and `.../reject` | R9 builds it | No route in this unit. The call site is one for two routes. |
| 15 | `POST api/sessions/{id}/people-proposals/respond` | R9 builds it | No route in this unit. |
| 16 | `POST api/sessions/{id}/cleanup/stream` | R9 builds it | No route in this unit. |
| 17 | `POST api/sessions/{id}/cleanup` | R9 builds it | No route in this unit. |
| 18 | `PUT api/sessions/{id}/cleaned` | `PUT /api/v1/sessions/{id}/cleaned` | Send `text`, `baseRevision`, and `savedAt`, not `cleanedText` and `clientSavedAt`. Fixture: `cleaned-save`. |
| 19 | `POST api/sessions/{id}/cleanup/result` | R9 builds it | No route in this unit. |
| 20 | `PUT api/sessions/{id}/metadata` | `PUT /api/v1/sessions/{id}/metadata` | Send `userTopics`, not `topics`, and `savedAt`. A `savedAt` that is not newer than the stored one changes nothing, and the reply has the stored Session. The People of a Session come from its mentions, so the body has no list of people. Fixture: `session-metadata`. |
| 21 | `GET api/sessions/{id}/revisions` and `.../cleaned-revisions` | `GET /api/v1/sessions/{id}/raw-revisions` and `.../cleaned-revisions` | The body is `{ items, nextCursor }`, newest first. An item has `number` and `savedAt`, not `revisionNumber` and `createdAt`, and adds `length`. Fixtures: `raw-revision-page`, `cleaned-revision-page`. |
| 22 | `GET api/sessions/{id}/{revisions path}/{number}` | `GET /api/v1/sessions/{id}/raw-revisions/{number}` and `.../cleaned-revisions/{number}` | `text`, not `content`. Fixtures: `raw-revision`, `cleaned-revision`. |

## Fields that this unit does not add

`SessionDto` has no `suggestions` and no `peopleProposals`. R9 owns rows 14, 15, and 19, and it adds these fields and their routes. Until then, the client must read them as absent.

## Journaling day

A change of the time zone moves a Session to a new day, but the sync feed does not send the Session again. The feed sends the settings row. Derive the day from `startedAt` and the zone of the settings row, or from UTC when `timeZoneIsKnown` is false. Do not keep `journalingDay` from an older response.

## New routes

- `DELETE /api/v1/sessions/{id}` removes a Session. The feed then has a tombstone. See [ADR 0020](adr/0020-journal-rules-in-the-database.md).
- `POST`, `GET /api/v1/people`, and `GET`, `PUT /api/v1/people/{id}`. A rename changes each mention. Fixtures: `person`, `people`.
- `POST`, `GET /api/v1/corrections`, and `GET`, `PUT`, `DELETE /api/v1/corrections/{id}`. Fixtures: `correction`, `corrections`.
