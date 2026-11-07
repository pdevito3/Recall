# Drafts use last-write-wins on the save time, not a version check

A person dictates on a phone that can stay offline for days. Each other command in this repository sends the `Version` of the row, and a stale version gives 409 (see [concurrency](../backend/concurrency.md)). The client then shows an error and the person loads the record again. On a Draft, that error tells a person that the words they spoke did not save.

So a save of a Draft sends no `Version`. It sends a base Revision and the save time on the client. The server applies these rules ([sessions spec](../specs/sessions.md), SES-5 to SES-8):

- A save that is based on the current Draft wins.
- A save that is based on an older Draft wins when its save time is not before the save time of the Draft.
- A save with a base that names no Draft loses at any save time. The column `was_draft` on a Revision says which numbers were a Draft. A lost save takes a number, so a base of `latestRevision` would otherwise win under the time rule and push a chunk out of the Draft.
- A Raw save from a base older than the last chunk loses at any save time, so a chunk stays in the Draft ([REC-6](../specs/recordings.md)).
- A win never moves the save time of the Draft back.
- A save that loses changes no Draft, and its words stay as the newest Revision. No words go away.
- A replay of a save makes no Revision. The first Revision that holds the words answers it. A Revision that was a Draft at the base or later says `Kept` with its number, and a Revision that lost with the same `savedAt` says `Lost`. So the answers come from the Revisions, and no new table holds them. A replay of a save that first said `Unchanged` wrote no row, and it says `Kept` from the Revision of the same words. The empty Draft has the number 0 and no Revision, so it answers an empty save at base 0 in the same way. SES-8 states the replays of `Unchanged` that do not give this answer.
- A save time in the future becomes the time of the server, so a fast clock cannot win each later save.

The server marks the save commands `IRunsAgainWhenRaced`, so the `Version` of the Session row still orders two saves at the same moment. The row version protects the server, and the save time decides between the words of the person.

## Settings and metadata use a base version

The metadata of a Session and the settings of a User followed the same save time rule until issue 40. That was wrong for them. They have no Revisions, so a change that goes away is gone, and each save replaces the whole row. So a body from a stale copy of the row brings back the old value of each field that the person did not change. This needs no wrong clock. On the rule that this section replaces, a phone turned location capture off, a tablet with a stale copy then changed the zone, and the tablet got a 200. Capture was on again, and the next Session stored a location. A stale metadata body dropped a Topic in the same way. Location capture is a privacy setting, so a stale value must not turn it back on.

The rule also lost changes in other ways, each with a 200. A change with a time that is not newer went away. A different change with the exact time of a kept claim went away. A change that was made earlier on a fast clock and arrived later replaced the newer change, and two overlapping PUTs from one device reached this with no failed request (14 of 60 runs at +1 h ended with capture on at the base commit of this change). A server clock that went back lost a change. To limit this, each row kept a list of the times that fast clocks claimed, with no cap.

So these saves now send a base version, with the rules in the [sessions spec](../specs/sessions.md), SES-31 to SES-35:

- A settings save sends the `version` of the row, and the entity calls `GuardVersion`. This is the rule of [concurrency](../backend/concurrency.md) for a PUT that replaces the fields of a form.
- A metadata save sends `metadataVersion`. The `version` of the Session row moves on each Draft save, so it cannot be the base. An offline queue that drains a Draft save first would get a refusal for each metadata save behind it. So `metadataVersion` moves only when a metadata change applies.
- A save from another version gets 409 and changes nothing. A refused metadata save stays out of the Draft routes, so no words go away.
- A save from another version with the values that the row already holds does nothing, and it gets 200. So a retry after a lost reply is safe. A retry after a newer change holds other values, so it gets 409.
- A save of the stored values from the current version moves the version. If it did not, an overlap of on and off from one read could apply off as no change and then apply on, with two 200s. The cost is a row in the sync feed for a save that changes no value, and a reload for another device that holds the old version.
- A body with no version gives 400. The `required` keyword does this, as for each other form PUT.
- The server ignores a `savedAt` on these two routes, so the columns of the save time and the list of claims are gone.

A person sees a refusal, not a lost change. This costs a reload where the old rule let the later save win, for example when two devices change different settings. A client reloads the row after a 409 and applies again only the fields that the person changed. See [iOS API changes](../ios-api-changes.md#settings-and-metadata-saves).

These saves differ from a Draft save because of what a refusal costs. A refusal of a Draft tells a person that the words they spoke did not save. A refusal of a Topic or a setting costs a tap. A refused change that turns location capture off costs more, because capture stays on until the change applies, so the client applies it again with no prompt and tells the person. This can undo a newer change on another device that turned capture on. The Sessions then store no location, and that direction is the safe one for privacy.

## Consequences

- `GuardVersion` is not on the Draft routes, and `SessionDto.Version` is only for a client that wants it. A later write command on a Session that has no save time must use `GuardVersion`, or a version of its own part of the row, as `metadataVersion` is.
- The clock of a phone can decide a Draft. A slow clock can lose to an older save. The words of that save stay as a Revision, so a person can get them back.
- Two writers from an older base can each get `Won`, and a word of one writer leaves the Draft with no signal. SES-5 states this limit.
- Revisions grow with each losing save and each autosave, and nothing prunes them.
- On a settings save or a metadata save, a 200 means that the row holds the values that the client sent, and a 409 means that nothing changed. A client that is offline for a long time gets a 409 when another device changed the row, and it must ask the person or apply the change again on the new row.
- A client that does not send a version gets 400 on these two routes. The POC client and any client of the body before this change fail this way until they send a version.
