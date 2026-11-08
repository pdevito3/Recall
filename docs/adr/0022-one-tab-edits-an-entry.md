# One tab of a browser edits an entry at a time

Before this decision, each tab of a browser could edit the same entry. A page that opened copied the words of a live tab, and both tabs then saved. Two tabs with the same unsent words merged them twice after `Lost`, so words showed twice. A merge with no known base, a lookup of Revisions by `savedAt` and text, and merge rules for two tabs tried to repair this. Each repair fixed some cases and made others (R12 fix6 to fix9).

So one tab of a browser edits an entry at a time:

1. The tab that edits holds a Web Lock of the entry, `recall.edit.<person>.<entry>`. A second tab that opens the entry shows the words of the editor with no edit, and an `Edit here` button. On plain HTTP the browser has no Web Locks, so a mark in `localStorage` names the editor.
2. `Edit here` steals the lock. The tab that loses goes read-only at once. It hands its unsent words, its change in flight, and its base to the new editor on the `recall.editors` channel, and it keeps them until the new editor says that it took them.
3. The editor writes its words and their exact base to its key at each change. A crash or a close frees the lock, and the next tab takes the words from the key.
4. A key holds the newest words of its saver or nothing. A failed write removes the older key when it holds other words, and the page says that the words are only on this page.
5. Each state of the words has an ID, with the IDs of the states it grew from. A tab takes each state once.

A merge after `Lost` now always has the base of the words. It stays the three-way merge of R12 fix8, and the rules for no base and for two tabs are gone.

## Considered options

- **Keep many editors and repair the merge.** Two tabs that hold the same words cannot tell their words from the words of the other tab. Change IDs on the server could tell, but they need a new column, and the server would then know about tabs.
- **One editor per browser for all entries.** A person who opens two entries in two tabs would see one of them read-only for no reason.

## Consequences

- Each case that the model test `draft-saver.model.test.ts` covers keeps each word once: a steal of a live editor, a frozen editor that wakes after a steal, a crash or a close of the editor, a full storage with a steal, and a crash of the tab that stole.
- A hand-over can hold words of a channel that the taking tab has no saver for, as when the person wrote in Cleaned and the second tab shows Your words. A helper of that channel takes the words with their own base, and the taker says that it took only the states that a saver took. A state that no saver took stays with the tab that gave it.
- A taker with no room in storage keeps the handed key while its words do not change, so a take does not move words from storage to memory.
- `Edit here` asks once while the hand-over is on its way. A tab whose take another tab stole during the wait waits in line again.
- These cases are not covered:
  - A crash with a full storage loses the words that only the memory of the tab holds. The page says so before the crash.
  - A sign-out cannot name the words that only the memory of a frozen tab holds.
  - On plain HTTP, a mark of a tab that crashed stays. The next tab needs `Edit here`.
  - `localStorage` of two processes of one browser can lag. Then a tab can read an older key.
