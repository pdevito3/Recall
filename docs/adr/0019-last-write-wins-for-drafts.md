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

A different change with a time below a kept claim also does nothing, because the server cannot tell it from a queued older change. This applies only to a time more than 5 seconds ahead of the server. A clock that is a little fast is common, and without this allowance one claim from a far future would stop each later change from that clock.

A different change with a time in the future that is below a kept claim also does nothing, because the server cannot tell it from a retry. After the server passes its time, it compares by that time, the same as an offline change from a good clock.

## Consequences

- `GuardVersion` is not on these routes, and `SessionDto.Version` is only for a client that wants it. A later write command on a Session that has no save time must use `GuardVersion`.
- The clock of a phone can decide a Draft. A slow clock can lose to an older save. The words of that save stay as a Revision, so a person can get them back.
- Revisions grow with each losing save and each autosave, and nothing prunes them.
- A row keeps one claim for each change from a fast clock until the server reaches it, so a clock years ahead grows the row of its owner for that time. It has no cap, because a cap lets a retry undo a newer change again.
- A clock that is more than 5 seconds fast cannot change a row that keeps a later claim until the server reaches its time. The reply is 200 with the stored row, so a client must compare it. Two changes within 5 seconds of the server and sent out of order can apply in the order that they arrive.
