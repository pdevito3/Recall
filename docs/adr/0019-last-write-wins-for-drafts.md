# Drafts use last-write-wins on the save time, not a version check

A person dictates on a phone that can stay offline for days. Each other command in this repository sends the `Version` of the row, and a stale version gives 409 (see [concurrency](../backend/concurrency.md)). The client then shows an error and the person loads the record again. On a Draft, that error tells a person that the words they spoke did not save.

So a save of a Draft sends no `Version`. It sends a base Revision and the save time on the client. The server applies these rules ([sessions spec](../specs/sessions.md), SES-5 to SES-8):

- A save that is based on the current Draft wins.
- A save that is based on an older Revision wins when its save time is not before the save time of the Draft.
- A save that loses changes no Draft, and its words stay as the newest Revision. No words go away.
- A replay of a save makes no Revision.
- A save time in the future becomes the time of the server, so a fast clock cannot win each later save.

The server marks the save commands `IRunsAgainWhenRaced`, so the `Version` of the Session row still orders two saves at the same moment. The row version protects the server, and the save time decides between the words of the person.

The metadata of a Session and the settings of a User follow the same save time rule, because a client also queues them offline. They have no Revisions, so the older change goes away. A change with the time of the stored change also goes away, so a retry cannot undo the change.

A time in the future gets the time of the server, so a retry of it gets a later time and looks newer than the changes between them. So a row keeps each claimed time that is ahead of its save time, and a retry of one of them does nothing, for any count of newer changes. When the save time passes a claim, the row drops it, because the time compare now refuses it. One high-water mark cannot do this: under the mark, a late retry and a new change from a good clock look the same.

A kept claim stops only a change with its exact time, at any distance from the server. So the time that one wrong clock can block a change with another time is zero. A claim from 2099 does not lock a row for years, and location capture off from a fast clock takes effect. The one exception is a client that reuses one time for each change. Each change after the first goes away, and a save time that passes that time does not end this. The row then drops the claim, but the time is not newer, so that time never applies again. Other clients are not stopped. While the server clock does not go back, a fast change that arrives first applies, so the server has no ignored first try that a later retry could apply.

The server could refuse a time far ahead with a 422 and keep no such claim. It does not, because a server with a slow clock would then refuse each client.

A person can still lose a change with a 200 in these cases, and the reply shows the row that the server has:

- A change with a time that is not newer than the stored change goes away, as from a slow clock or a queue.
- A different change with the exact time of a kept claim goes away, because the server cannot tell it from a retry.
- The time of a change from a fast clock is later than its edit. While that time is ahead of the server, the change gets the time of the server, so the order of arrival decides. After that, it keeps its own time. Either way, a change that was made earlier and arrives after a newer change replaces the newer change. This needs no failed request and no second device, as the next section shows.
- If the server clock goes back, a change with a time ahead of the server gets a save time below the stored one, and it goes away. The server does not remember it, so its retry applies after the server passes the stored save time, also over a newer change.

## Overlapping changes from one device

Two overlapping PUTs from one device reach the third case above. No request fails, and the client reaches the server each time. We measured settings on localhost with no injected delay, at commit 429e5c6a. Each run sent capture on and then capture off. This table counts the runs that ended with capture on:

| Clock of the client | Time between the two PUTs | Ended with capture on |
|---|---|---|
| Right | 0, 1, and 5 ms | 0 of 240 |
| +2 s | Started together | 28 of 60 |
| +6 s | Started together | 30 of 60 |
| +1 h | Started together | 22 of 60 |
| 2099 | Started together | 23 of 60 |
| +6 s | 5 ms | 0 of 80 |
| +1 h | 5 ms | 0 of 80 |

The 5 ms rows do not make a gap safe, because they come from localhost. On another run, the PUT for off went 1 s after the PUT for on, and the PUT for on landed 2 s after the PUT for off. Capture ended on at each fast clock in the table, and the next Session stored a location. With a right clock, capture stayed off. For metadata, the older body also replaced the newer body at each fast clock when it landed second.

So a client must send the settings or metadata changes of a row one at a time and in order, and wait for the reply to each change before it sends the next. After a failure with no reply, it sends that change again with the same time before it sends a newer change. A client that drained a queue in this way at +1 h, with a lost reply and stalled duplicates, kept the newest change for settings and for metadata.

The POC `UserSettingsStore.execute` does not do this, because it starts a PUT for each edit and does not wait for the one before it. R14 must do this.

This rule does not stop the case on a second device. A fast phone that edits first and lands second replaces the edit of a tablet when the phone clock is ahead by more than the time between the two edits. We measured this at +6 s with 3 s between the edits, and at +1 h with 20 s. At +2 s with 3 s between the edits, and at +6 s with 8 s, the edit of the tablet stayed.

## Consequences

- `GuardVersion` is not on these routes, and `SessionDto.Version` is only for a client that wants it. A later write command on a Session that has no save time must use `GuardVersion`.
- The clock of a phone can decide a Draft. A slow clock can lose to an older save. The words of that save stay as a Revision, so a person can get them back.
- Revisions grow with each losing save and each autosave, and nothing prunes them.
- A row keeps one claim for each change from a fast clock until a save time of the row passes it, so a clock years ahead grows the row of its owner for that time. It has no cap, because a cap lets a retry undo a newer change again.
- A 200 does not say that a change applied. A client that sees a reply that differs from what it sent must show the reply and must not send again with a new time, because that can undo a newer change.
