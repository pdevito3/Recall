# The database keeps the journal rules, and a delete removes the words

"A person expects a deleted journal entry to be gone." The soft delete of `BaseEntity` keeps the row, so it cannot apply to a journal. A journal also has rules that no code path can forget: a Revision never changes, the sync feed has a line for each change, and the journal goes when its owner goes. A handler can forget a rule, and a raw statement or a later job does not run the handler.

So these rules are triggers in the database. [`JournalTriggers`](../../Recall.Server/Databases/JournalTriggers.cs) holds them, and the first migration calls it:

- **Sync feed.** A trigger on each journal table writes one line to `sync_changes` for each insert, update, and delete. A second change of a row replaces its line, with a new `AUTOINCREMENT` sequence. So a pull gives each row once, in the order of its newest change, also when two saves have one timestamp. A read needs no clock.
- **Append-only.** A trigger refuses an update of a Revision, and a delete of a Revision whose Session stays.
- **Purge.** When a User is soft-deleted, also through a delete of the tenant, a trigger hard-deletes each journal row of the User and the lines of the feed.

## What a delete removes

A delete of a Session removes its row, and the foreign key removes its Revisions. The feed keeps a tombstone with the session ID and the client session ID, and no words. A replay of the create of that Session then gives 409, so a queued phone cannot bring it back.

Each connection has `PRAGMA secure_delete = ON`, so SQLite overwrites the freed space. After a delete of a Session, a Correction, a User, or a tenant, the handler tries a `wal_checkpoint(TRUNCATE)`, which cuts the WAL to zero bytes and removes its old pages. A reader that holds on for more than the wait of 250 ms stops the truncate, and the handler does not retry. The old pages then stay in the database file or in the WAL until a later checkpoint passes the delete, the journal size limit of 16 MiB cuts the file, or the last connection closes.

A kill between the commit of a delete and the truncate leaves the words in the WAL. A boot alone does not clear them. So each start of the server runs the same truncate after its migrations and before Kestrel binds. It goes through the locks of SQLite and has the same wait. If another process holds the WAL, the start goes on and logs that it did not scrub, and the words stay until a later checkpoint passes them.

What stays:

- The audit log has the IDs of the row and no words. `AuditModel` records only `TenantId` and `UserId` of a journal row.
- A backup keeps the words until the backup rotates out. A restore brings them back.
- The server does not run `VACUUM`. `secure_delete` makes this safe, and the file does not get smaller.

## Sync retention

Tombstones stay until their owner goes. So no cursor is ever too old, and the server has no "too old" reply. A cursor that is ahead of the newest line of the User can come only from a restore of a backup. It gives 422, the one 422 of the feed, and the client drops the cursor and its synced rows, keeps its unsent saves, and pulls from the start. A restore that is followed by enough new writes to pass the cursor of a phone goes unseen, and that is a known limit.

## Consequences

- An EF table rebuild drops the triggers of the table. A migration that rebuilds a journal table must call `JournalTriggers` again. `JournalTriggerTests` compares `sqlite_master` with the model and fails when it does not.
- A rule is not visible in the C# of a handler. The [sessions spec](../specs/sessions.md) lists each one.
- The sequence is one counter across all Users, so a cursor shows how many changes the server made. It shows no words and no IDs.
- An EF table rebuild of `sync_changes` sets the counter to the newest line that stays, so it can go back after the purge of a User. No client skips a change, because a cursor of a live User is never above that User's newest line, and a line leaves only with its owner.
- A tombstone stays for each deleted Session, and it holds two IDs.
- Each start pays for one checkpoint, which writes at most the size of the WAL.
