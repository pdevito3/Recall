# Journal

The app opens on the journal. A person writes at once, and each entry saves as the person types. A person reads, edits, tags, restores, and deletes entries on a phone width and on a desktop width.

## Sub-features

- `capture` starts an entry at the first word in `New entry`. `Done` or Ctrl+Enter (Cmd+Enter on macOS) ends it and gives an empty box.
- `autosave` shows `Not saved yet`, `Saving`, and `Saved at <time>`. A reload within an hour returns the words to `New entry`.
- `timeline` shows the entries by day, newest first, and loads more entries near the end of the list.
- `entry` opens at `/sessions/<id>` with the editor `Your words`, the toolbar `Formatting`, `History`, and `Tags`.
- `conflict` shows a notice when a save from another tab or device meets this page. Each save stays in the history.
- `history` lists the versions. A version opens in a dialog with `Restore this version`.
- `tags` changes moods, activity, and topics after `Edit tags`.
- `delete` asks in a dialog with `Delete entry` and `Keep entry`.
- `offline` shows `Not saved yet. Recall keeps trying. Keep this page open.` until the save ends.
- `file-store` shows a notice with what to do when `GET /api/v1/file-store/status` gives `canWrite: false`.

## How to get to it (user POV)

- Open `/`, or choose `Journal` in the sidebar.
- Choose a row of the timeline to open an entry.
- Choose `Show history`, `Edit tags`, or `Delete` on an entry.

## Driving it with browse.mjs

Preconditions:

- `doctor.sh` exits with 0.
- The recipe logs in and makes the entries that it needs.

- **Capture.** Run `page.keyboard.type(words)` on `/`. `New entry` has the focus. The status region shows `Saved at <time>`. Choose `Done`. A link with the words shows in the timeline.
- **Autosave.** Type, wait for `Saved at <time>`, and run `page.reload()`. `New entry` has the same words.
- **Two tabs.** Open one entry in two pages of the context. Type and save in one, then in the other. The first save wins. The save of the second page gets `Lost`, so that page reads the entry, adds its words to the newest words one time, and saves again. It shows `Saved at <time>` and a note that starts with `Another tab, device, or recording changed this entry`, and each page shows the words of both, each one time. `Show history` lists each save, and the save that lost is a version of its own.
- **Restore.** Choose a version that the page does not show now, such as the save that lost in Two tabs, then `Restore this version`. `Your words` has the words of that version, also after a reload.
- **Delete.** Choose `Delete`, then `Delete entry`. The page goes to the journal, and `GET /api/v1/sessions/<id>` gives 404.
- **Offline.** Run `context.setOffline(true)`, then type. The status region shows `Recall keeps trying`. Run `context.setOffline(false)`. The status region shows `Saved at <time>` within 35 seconds.
- **Phone.** Set the viewport to 390x844. The page has no side scroll, and each control is at least 44 px in each direction.
- **Keyboard.** Use only `Tab`, `Enter`, `Space`, and `Escape`. Each stop shows a focus ring.
- **Proof.** Run `node $S/scripts/browse.mjs $S/recipes/journal.mjs`. The captures `r12-create`, `r12-autosave`, `r12-conflict-notice`, `r12-conflict`, `r12-revision`, `r12-delete`, `r12-offline`, `r12-phone`, and `r12-keyboard` show each state.

## Gotchas

- A `page.route` mock still answers while `context.setOffline(true)` is on. Prove the offline state on a live stack, not with a mock.
- `journal.mjs` cannot prove suggestions. R9 owns them.
- The composer keeps a pointer to its entry in `sessionStorage`, with no words, so a second recipe step in the same tab can resume it. Unsent words stay in `localStorage` under the key of the owner, until the Server holds them. Choose `Done` before a step that needs an empty box.
- A jump to the end of the list settles row heights for one frame. To measure a jump, scroll with `page.mouse.wheel`.
