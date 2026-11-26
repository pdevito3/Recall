# Spec: imports

## 1. Scope

A person has years of journal text in another app. An AI agent reads that source, splits it into entries, and writes them into an Import batch in Recall. The person reviews the batch in the SPA, and one accept makes each kept entry a Session of the journal. Recall does not parse a source. It gives the agent the tools, the person the review, and the agent a doc and a skill. [Issue #109](https://github.com/pdevito3/Recall/issues/109) gives the reasons. [ADR 0026](../adr/0026-an-import-batch-is-a-draft-area-outside-the-journal.md) records the design.

The import changes two rules of [the journal spec](sessions.md). The phase that builds each change also changes the rule:

- SES-3: the accept keeps the zone of the import, also when the settings have a Fixed zone (IMP-10).
- SES-15: its MCP test changes from "no MCP tool is in a journal domain" to "each MCP tool of a journal domain is for a caller with a journal" (IMP-22). The ban on a gRPC service stays.

This spec leaves out:

- a parser of any source in Recall;
- a bulk Cleanup of the Sessions of a batch, which is a later issue;
- an import of Persons, Recordings, audio, Cleaned, or Summaries;
- an undo of an accept. The person deletes the Sessions one by one.

## 2. Entities and states

| Entity | Root of | Fields that a user sets | States |
|---|---|---|---|
| `ImportBatch` | `ImportEntry`, `ImportDay` | name, time zone | Open, Accepted |
| `ImportEntry` | | The agent: client session ID, date, position, time, time zone, Raw, Moods, Topics. The person: rejected, words, time. | None. A rejected entry is a flag of the review. |
| `ImportDay` | | date, rejected | None |
| `UserSetting` | | import review | None |

Each of the three import types is `IJournalOwned` and has a `user_id`, so the `JournalOwner` filter reads each one, and none of them is in the Sync feed.

| From | To | Method | Who |
|---|---|---|---|
| Open | Accepted | `ImportBatch.Accept` | With the review on, the person in a web session. With the review off, each caller with journal write. |
| Open | (deleted) | `DiscardImportBatch` | Each caller with journal write |

An accept deletes the `ImportEntry` and `ImportDay` rows. The `ImportBatch` row stays, with its counts, as the mark of its Sessions.

## 3. Rules

| ID | Rule | Enforced by | Backstop |
|---|---|---|---|
| IMP-1 | A batch opens with a name and a time zone. The name is trimmed and has 1 to 200 characters. A zone that this host does not know, or that is not the zone of a place, such as `Factory`, gives 422. A batch has no expiry and no limit on its entries. IMP-27 limits the accept. | `ImportBatch.Open`, `ImportZone.Parse` | |
| IMP-2 | No entry of a batch is in the timeline, a Session read, search, a Summary, a Cleanup, or the Sync feed before the accept. The import tables have no Sync trigger, and no reader of Sessions reads them. Each import table has the owner guard of a journal table. | Own tables for `ImportBatch`, `ImportEntry`, `ImportDay` | `JournalTriggerTests` with the three types in `JournalTablesOutsideTheFeed`, `JournalTriggers.AddOwnerGuard` on each import table |
| IMP-3 | A put adds or changes the entries of one open batch by Client session ID. Each entry gets one outcome of IMP-28. The same call again gives the same outcome for each entry that it did not store, `Unchanged` or `Rejected` for each entry that it stored, and changes nothing. A put removes no entry. | `PutImportEntries` | |
| IMP-4 | A User has one space of Client session IDs for Sessions and open batches. An entry whose ID is a Session gets `InJournal`, an ID with a Tombstone gets `Deleted`, and an ID in another open batch gets `InOtherBatch`. None of the three is stored. So a second run of a whole import after an accept stores only the new entries. | `ImportIdentity.Classify` | Unique index on `import_entries.user_id, client_session_id` |
| IMP-5 | The agent writes the agent layer of an entry: the date of the source day, the position in that day from 0 to 719, a time of day only when the text gives one, a zone or none, Raw, the Moods, and the Topics. A put replaces the agent layer and never changes the review layer (IMP-6). | `ImportAgentLayer.Of`, `ImportEntry.PutFromAgent` | |
| IMP-6 | The person writes the review layer with the review routes, and no other route writes it. A review of an entry sends its whole review: `rejected`, the edited words or null, and the edited time or null. Null clears an edit. A review of a day sends `rejected`. A rejected day holds each entry of that date, also an entry that a later put adds. A put that changes the date of an entry in a rejected day stores nothing for that entry, and the entry gets `Rejected`. An entry is rejected when its own flag or its day is rejected. An entry that the person rejected alone stays rejected when its day is no longer rejected. The last review wins, and a review sends no version. The words of an edit have the limit of IMP-11, and are not blank. | `ImportEntry.ChangeReview`, `ImportReview.Of`, `ImportBatch.ReviewDay` | |
| IMP-7 | The start time of an entry is the time of the person, else the time of the agent, else 12:00 plus `position` minutes, on the date of the entry in its zone. So entries at positions 0, 1 (at 9pm), and 2 start at 12:00, 21:00, and 12:02. A time in a spring-forward gap moves to the first minute after the gap. A time that occurs twice at a fall back takes the first. The server derives the start time at each read and once at the accept, and it does not store it. So a reject, an edit, or a put never moves the start time of another entry. | `ImportStartTime.Of` | |
| IMP-8 | Two entries of one date in a batch do not have the same position. A put checks the state after the whole call, so a call can swap two positions. A stored entry that the call does not name, and an entry that the call refuses, keep their stored position. When an entry that keeps its stored date and position shares them with entries of the call, it stays, and each entry that moves into them gets `PositionTaken` with the ID of the entry that stays. In another clash, each entry of the call that shares its date and position gets `PositionTaken` with the ID of another entry. An entry that gets `PositionTaken` is not stored. The check repeats until it refuses no new entry. | `PutImportEntries`, `ImportPositions.Check` | The version of the batch (IMP-17). No unique index, because SQLite checks a unique index on each row, and a swap passes through a duplicate. |
| IMP-9 | An entry whose start time is in the future gets `InTheFuture` and is not stored. A review with a time that puts the start in the future gives 422. | `PutImportEntries`, `ImportEntry.ChangeReview` | |
| IMP-10 | The zone of an entry is its own zone, else the zone of the batch. A Fixed zone of the settings does not override it, because the day comes from the date of the source. An entry zone goes through the check of IMP-1, and an entry that fails it gets `Invalid` on `timeZone`. | `ImportEntry.ZoneOf`, `ImportZone.Parse`, `AcceptImportBatch` | |
| IMP-11 | The agent sends Raw as Markdown with no change to the words of the person. The entry stores that Markdown, and a list of entries gives it back exactly. The accept stores the ProseMirror document of `MarkdownToProseMirror`, which keeps each word. An edit of the words by the person is a ProseMirror document, and it wins at the accept. Raw has at most 1,000,000 characters, the limit of a Draft, and its document has words and fits the limits of a Draft. | `ImportAgentLayer.Of`, `ImportEntry.RawAtAccept`, `MarkdownToProseMirror`, `ProseMirrorToPlainText` | |
| IMP-12 | A read of entries shows `wordsChangedAfterEdit` when a put changed Raw after the last edit of the words by the person, and `timeChangedAfterEdit` for the time in the same way. A new edit clears the flag. | `ImportEntry.PutFromAgent`, `ImportEntry.ChangeReview` | |
| IMP-13 | A remove deletes the named entries of an open batch. An ID that the batch does not hold gets `NotFound`. A rejected entry (IMP-6) stays, with its reject, and gets `Rejected`, so the agent cannot undo a reject. | `RemoveImportEntries` | |
| IMP-14 | The accept is one save in the shared tenant lock. It makes one Session for each kept entry, with Raw as Revision 1, the start time, the zone, the Moods, the Topics as AI Topics, and the ID of the batch as `import_batch_id`. A kept entry is an entry that is not rejected and whose day is not rejected. A Session of an accept has Cleanup status NotRun. The accept writes no Person and starts no Cleanup. The Sync feed carries the Sessions through the existing triggers, and a stored Summary over their days reads Stale. | `AcceptImportBatch`, `ImportBatch.Accept` | `RootLock.Tenant` |
| IMP-15 | `sessions.import_batch_id` has an index and no foreign key, so its migration adds a column and does not rebuild `sessions` or drop its triggers. | `SessionConfiguration` | `JournalTriggerTests` |
| IMP-16 | In the lock, the accept classifies each kept entry again by IMP-4. An entry whose ID became a Session or got a Tombstone after its put makes no Session, and the counts of the result name it. `AddSession` does not read the import tables, so a device Session with the same ID wins over the entry. An accept whose save loses to such a Session, while the version of the batch did not move, runs again once and skips the entry. | `AcceptImportBatch`, `ImportIdentity.Classify` | Unique index on `sessions.user_id, client_session_id` |
| IMP-17 | Each put, remove, and review that changes a row of the batch, its entries, or its days also moves the version of the `import_batches` row in the same save. A write that changes no row, such as a put that gives only `Unchanged` and refusals, does not move it. So two writes to one batch cannot both save from one read, and the loser runs again. Each write returns the new version. The accept checks in this order: an accepted batch returns its stored result with 200 and writes nothing, also to an accept that another accept beat; then the gate of IMP-18; then the limit of IMP-27; then the version that its caller read. An accept from another version gets 409 with the type `urn:recall:problem:stale-version` and changes nothing. | `AppDbContext`, which moves the version of the owner of a changed child, and `ImportBatch.Accept` | `import_batches.version` as a concurrency token |
| IMP-18 | With the review on, only the person in a web login session, which a PAT or a native token cannot get, accepts a batch. A native session, a PAT, and a provider token get 403. With the review off, each caller with journal write accepts, and the audit log records the same fact. | `AcceptImportBatch` reads the `client_kind` claim | |
| IMP-19 | The import review is on for a new User and for each User from before the setting. `PUT /me/settings/import-review` changes it, from the `version` of the settings by SES-31, and only a web session can call it. Only a web session turns it on, too, so the rule has one gate, on purpose. `PUT /me/settings` does not change it, so the iOS app needs no change. `GET /me/settings` shows it. | `UserSetting.ChangeImportReview`, `ChangeImportReview` | |
| IMP-20 | The accept records one `import_batch_accepted` action on the batch, with the counts of the Sessions made, the entries rejected, the days rejected, and the entries skipped by IMP-16. Each Session insert of the accept holds only its owner, by the audit rule of journal rows. The action and the inserts have one request and one transaction. | `ImportBatchAccepted`, `AuditModel` | |
| IMP-21 | A discard deletes an open batch and each of its rows. A reject and a discard leave no Tombstone, so after the accept or the discard of a batch, a rejected ID is free for a later import. | `DiscardImportBatch`, `ImportBatch.Accept` | No Sync trigger on the import tables |
| IMP-22 | Each MCP tool of a journal domain has `ToolAccessKind.JournalRead` or `ToolAccessKind.JournalWrite`. `tools/list` shows a `JournalRead` tool, and a call runs, only for a caller with a journal. A `JournalWrite` tool also needs journal write, so a PAT with Read journal access sees only the list tools (PAT-6, PAT-7). A machine agent, a delegated agent, an all-tenants client, a system scope, and a PAT with no journal access see no import tool. | `McpToolAccess`, `ToolAccessKind`, `CallerIdentity.Journal` | `JournalRulesTests.each_mcp_tool_of_a_journal_domain_is_for_a_caller_with_a_journal`, `ImportBatchMcpTests` |
| IMP-23 | A put, a remove, a review, and a discard of an accepted batch give 409 and change nothing. Each import write calls `RequireJournalWriter` before it reads, so a caller with Read journal access gets 403 also on a write that would change nothing. | `ImportBatch.EnsureOpen`, `RequireJournalWriter` | `JournalWriteRule` |
| IMP-24 | A delete of a User, also through a delete of the tenant, removes each batch of the User and its rows. | | `users_purge_journal` deletes from `import_entries`, `import_days`, and `import_batches` |
| IMP-25 | No word of an entry, no name of a batch, and no Raw reaches a log, a span, or an audit value. | `AuditModel`, each handler | Test with a marker through each import route |
| IMP-26 | A list of journal days gives each Journaling day from a start date to an end date, both included, that has a Session, with the count of its Sessions. A range of more than 366 days, an end before the start, or a date outside 1000-01-01 to 9998-12-31 gives 422. The agent uses it to find an overlap with the journal. | `GetJournalDays` | |
| IMP-27 | An accept of more than 20,000 kept entries gives 422 and changes nothing, so that one accept ends inside the 30 s limit of the writer. The person splits the batch, for example by year. | `ImportBatch.Accept` | |
| IMP-28 | The outcome of a put entry is the first that applies: `Invalid` with the field and the reason, `InJournal`, `Deleted`, `InOtherBatch`, `InTheFuture`, `PositionTaken`, `Rejected`, `Unchanged`, `Changed`, `Added`. `Invalid` covers a Client session ID that is blank or has more than 100 characters, a date that is not valid or is outside 1000-01-01 to 9998-12-31, a position outside 0 to 719, a time that is not `HH:mm`, a zone of IMP-10, a Raw of IMP-11, and Moods or Topics over the limits of a Session (50 values of at most 100 characters). Only `Rejected`, `Changed`, and `Added` store the agent layer. A whole call gets 422 when it names one Client session ID twice or holds more than 500 entries, and 413 when its HTTP body, on REST or on `/mcp`, is more than 16 MB. A client sure to read the 413 sends `Expect: 100-continue`. A client that sends the whole body first can see a closed connection. | `PutImportEntries`, `ImportPutOutcomes.Of`, `ImportAgentLayer.Of` | The request size limit of the REST route and of `/mcp` |
| IMP-29 | A Session read and each row of the timeline (`GET /sessions`) carry `importBatchId`, null for a Session that no import made. The Sync feed does not carry it. | `SessionMapper`, `GetSessionPage` | |
| IMP-30 (later) | The `recall-import` skill names exactly the MCP tools of the import. | `frontend/src/domain/imports/recall-import.md` | A test that compares the skill with the tool list |
| IMP-31 | A list of the days of a batch gives each date that has an entry, and each rejected day with no entry, in date order. Each day has the count of its entries, the count of the entries that the person rejected one by one (IMP-6), and the reject of the day. The list has the version of the batch, so the SPA can show the kept and rejected counts of an accept and send that version with it (IMP-17). It is one grouped query, and it reads no words. | `GetImportDays` | |
| IMP-32 | A read of entries gives `document` for each entry: the words that the accept stores now, as a ProseMirror document. It is the edit of the person, else Raw through `MarkdownToProseMirror` (IMP-11). The review shows it and starts an edit from it, so the person sees what the journal gets. | `ImportEntry.RawAtAccept`, `ImportBatchMapper` | |

### The rules of the import view

The SPA shows the review of a batch at `/imports/{id}`, and the list of batches at `/imports`. A test of the SPA names its rule with `@IMP-n`.

| ID | Rule | Enforced by | Backstop |
|---|---|---|---|
| IMP-33 | The sidebar has the link Imports to `/imports`. The page lists each batch, the newest first, with its name, the label of its zone, its count of entries, its state (Open, or In your journal), and the time it opened, in the zone of the browser. With no batch, the page says that an agent opens a batch with a personal access token. | `ImportBatchList`, `AppSidebar` | |
| IMP-34 | Discard of an open batch asks first, with the name of the batch and its count of entries. After the discard, the batch leaves the list. An accepted batch has no Discard. | `DiscardImportBatch` (SPA) | |
| IMP-35 | The page of a batch shows its entries in groups by Journaling day, in the order of the date and then of the position. It cuts the days of IMP-31 into pages of whole days with at most 200 entries each. A day of more than 200 entries is a page alone. It reads each page with `from` and `to`, and the page number is in the URL. A rejected day with no entry shows as a day too. | `pagesOf`, `ImportReview` | |
| IMP-36 | Each entry shows its start time of IMP-7 in the zone of the entry, with the label of the zone when it is not the zone of the batch. When no person and no agent gave a time, the entry says that its time comes from its place in the day. The entry shows the words of `document`, its Moods, and its Topics. | `ImportEntryCard` | |
| IMP-37 | An entry with `wordsChangedAfterEdit` says that the import changed the words after the edit of the person, and that the accept keeps the edit. `timeChangedAfterEdit` says the same of the time. | `ImportEntryCard` | |
| IMP-38 | Edit opens the words of `document` and the time of the entry. Save sends the whole review of IMP-6: the reject as it is, the words only when the person changed them, else the words of the last edit, and the time in the same way. Use the import words and Use the import time send null for that edit. A refusal of the edit, such as a time in the future, shows in the form, and the form keeps the change. | `ImportEntryEdit` | |
| IMP-39 | Reject sends the whole review with `rejected` true, and Keep sends it with false. A rejected entry says so, and its words are dimmed. | `ImportEntryCard` | |
| IMP-40 | Reject day and Keep day send the review of a day. Each entry of a rejected day shows as rejected and has no Keep, because its day holds it. An entry that the person rejected alone stays rejected when its day is kept again. | `ImportDayGroup` | |
| IMP-41 | The page shows the count of the entries, the kept entries, and the rejected entries, from the days of IMP-31. After each review, the page reads the days and the entries of the page again, so the counts and the version change. | `ImportReview` | |
| IMP-42 | Accept asks first, with the count of the kept entries that go into the journal and the count of the rejected entries that stay out. It sends the version of the last read of the days. | `AcceptImportBatch` (SPA) | The version check of IMP-17 |
| IMP-43 | After the accept, and on each later visit, the page of an accepted batch shows the counts of its result: the entries made, the entries rejected, the days rejected, the entries skipped because the journal has them, and the entries skipped because the person deleted them. It has a link to the journal, and no review action. | `ImportResult` | |
| IMP-44 | A refusal of a review, an accept, or a discard shows the message of the Server of section 6 as it is. A 409 of IMP-17 on the accept keeps the dialog open with that message and reads the batch again, so the dialog shows the new counts. A 409 of IMP-23 and a 404 of IMP-6 read the batch again, and the message shows in a toast, also from the form of an edit. When the reread takes the entry off the page, the focus goes to the heading of the batch. A batch that does not exist shows that it does not exist, with a link to the imports. | `ImportReview`, `AcceptImportBatch` (SPA) | |
| IMP-45 | The settings have the import review of IMP-19 as a check box. Its text says that with the review on, only the person can accept a batch, in Recall in a browser, and that with it off an agent can accept a batch too. A change from an old version of the settings reads them again and sends the change once more. | `ImportReviewSetting` | |

## 4. Permissions

None. Each import route needs a caller with a journal (SES-15), and each write needs journal write (PAT-7). A PAT with Read journal access can list batches, entries, and days, and gets 403 on each write.

## 5. Endpoints and tools

| Feature | HTTP | MCP tool | Permission |
|---|---|---|---|
| `OpenImportBatch` | `POST /api/v1/import-batches` | `open_import_batch` | Journal write |
| `GetImportBatches` | `GET /api/v1/import-batches` | `list_import_batches` | A journal |
| `PutImportEntries` | `PUT /api/v1/import-batches/{id}/entries` | `put_import_entries` | Journal write |
| `RemoveImportEntries` | `POST /api/v1/import-batches/{id}/entries/remove` | `remove_import_entries` | Journal write |
| `GetImportEntries` | `GET /api/v1/import-batches/{id}/entries`, with optional `from` and `to` dates, both included | `list_import_entries` | A journal |
| `GetImportDays` | `GET /api/v1/import-batches/{id}/days` | None | A journal |
| `GetJournalDays` | `GET /api/v1/journal-days` | `list_journal_days` | A journal |
| `ReviewImportEntry` | `PUT /api/v1/import-batches/{id}/entries/review`, with the Client session ID in the body | None | Journal write |
| `ReviewImportDay` | `PUT /api/v1/import-batches/{id}/days/review`, with the date in the body | None | Journal write |
| `AcceptImportBatch` | `POST /api/v1/import-batches/{id}/accept` | `accept_import_batch` | Journal write, and IMP-18 |
| `DiscardImportBatch` | `DELETE /api/v1/import-batches/{id}` | `discard_import_batch` | Journal write |
| `ChangeImportReview` | `PUT /api/v1/me/settings/import-review` | None | A web session |

The review routes have no MCP tool, because the review is the work of the person. `GetImportDays` has no MCP tool, because only the review in the SPA reads it.

## 6. Messages

| Rule | Status | Message |
|---|---|---|
| IMP-1 | 422 | Give the batch a name of 1 to 200 characters. |
| IMP-1 | 422 | This server does not know this time zone. Pick another zone. |
| IMP-1 | 404 | This import batch does not exist. |
| IMP-6 | 404 | This entry is not in the batch. |
| IMP-9 | 422 | The time of an entry cannot be in the future. |
| IMP-17 | 409 | This batch changed after you read it. Read it again, then accept it. |
| IMP-18 | 403 | The import review is on, so only you can accept this batch, in Recall in a browser. |
| IMP-19 | 403 | Only you can change the import review, in Recall in a browser. |
| IMP-23 | 409 | This batch is in your journal, so it cannot change. |
| IMP-26 | 422 | Pick a range of at most 366 days, with the end on or after the start. |
| IMP-26 | 422 | Pick dates from 1000-01-01 to 9998-12-31. |
| IMP-27 | 422 | This batch has more than 20,000 entries to accept. Split it into smaller batches, for example one for each year. |
| IMP-28 | 422 | Send each entry once, and at most 500 entries in one call. |
| IMP-28 | 413 | This call is larger than 16 MB. Send fewer entries in each call. |
| IMP-28 | 413, on `/mcp` | This call is larger than 16 MB. Send less data in each call, for example fewer entries. |

## 7. Time zones

| Value | Stored as | Shown in the zone of |
|---|---|---|
| The date of an entry | `date`, the day of the source | None. It is a calendar day. |
| The time of an entry, from the agent or the person | Local time of day, or none | The zone of the entry |
| The zone of an entry, of a batch | IANA ID | |
| The start time of an entry | Not stored. IMP-7 derives it. | The zone of the entry |
| The start time of an accepted Session | UTC instant and the zone of the entry (SES-3) | The viewer, as for each Session |
| Opened, accepted | UTC instant | The browser |

## 8. Open decisions

None. On 2026-11-25 the operator set the limit of IMP-27 at 20,000 kept entries, after the measure of Phase 2.
