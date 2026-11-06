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
- The time of a change from a fast clock is later than its edit. While that time is ahead of the server, the change gets the time of the server. After that, it keeps its own time. Either way, a change that was made earlier and arrives after a newer change replaces the newer change. This needs a first try that did not reach the server and a client that sends the next change anyway, or a second device. A client that sends a row's changes in order and stops at a failure does not reach it from one device.
- If the server clock goes back, a change with a time ahead of the server gets a save time below the stored one, and it goes away. The server does not remember it, so its retry applies after the server passes the stored save time, also over a newer change.

## Consequences

- `GuardVersion` is not on these routes, and `SessionDto.Version` is only for a client that wants it. A later write command on a Session that has no save time must use `GuardVersion`.
- The clock of a phone can decide a Draft. A slow clock can lose to an older save. The words of that save stay as a Revision, so a person can get them back.
- Revisions grow with each losing save and each autosave, and nothing prunes them.
- A row keeps one claim for each change from a fast clock until a save time of the row passes it, so a clock years ahead grows the row of its owner for that time. It has no cap, because a cap lets a retry undo a newer change again.
- A 200 does not say that a change applied. A client that sees a reply that differs from what it sent must show the reply and must not send again with a new time, because that can undo a newer change.
