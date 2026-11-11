# Corrections

A person keeps the words that dictation gets wrong at `/corrections`: each term, the ways dictation mishears it, and whether Cleanup replaces each mishearing every time. The iPhone app edits the same Corrections, and each device shows the changes of the other.

## Sub-features

- `list` shows `Your corrections`, each term with the line `Heard as <mishearings>` and `Always replaced` when the flag is on.
- `empty` shows `You have no corrections yet. Add a word that dictation gets wrong.`.
- `search` finds a term or a mishearing with no regard to case or accents. The search is in the URL. No match shows `No correction has “<search>” in its term or its mishearings.`.
- `add` opens the dialog `Add a correction` from `Add correction`, with `Term`, `Mishearings` (one on each line), and `Always replace`. `Save correction` closes it and shows the toast `Correction saved`.
- `edit` opens `Edit <term>` from the button `Edit <term>` with the saved values. A save after a save on another device shows `This correction changed after you opened it.` and `Load the latest`.
- `delete` asks `Delete <term>?` from the button `Delete <term>`. `Delete correction` removes it, shows the toast `Correction deleted`, and puts the focus on the heading.

## How to get to it (user POV)

- Choose `Corrections` in the sidebar.
- Open `/corrections`, or `/corrections?search=<text>`.

## Driving it with browse.mjs

Preconditions:

- `doctor.sh` exits with 0.
- The database has no tenant, so `login()` makes an Admin with no Corrections.

- **Empty.** Choose `Corrections`. The empty text shows.
- **Add.** Choose `Add correction`. Fill `Term` with a long word with no spaces and `Mishearings` with two lines, check `Always replace`, and choose `Save correction`. The row shows the term of the `POST` answer, its mishearings, and `Always replaced`.
- **Search.** Fill `Search corrections` with a mishearing in other case. The row stays, and the URL has the search. A search with no match shows the no-match text.
- **Edit.** Choose `Edit <term>`. Add a line to `Mishearings`, clear `Always replace`, and choose `Save correction`. The `PUT` body has the version of the row, and the row shows the mishearings of the answer with no `Always replaced`.
- **Delete.** Choose `Delete <term>`, then `Delete correction`. The list shows the empty text, and the heading has the focus.
- **Proof.** Run `node $S/scripts/browse.mjs $S/recipes/corrections.mjs`. The captures `empty`, `added`, `searched`, `edit-dialog`, `edited`, and `deleted` show each state.

## Gotchas

- The sweep gives `canonicalTerm` a long value in each answer, so the recipe finds a row by the term of the answer, not by the typed term.
- The Server drops a blank mishearing, a mishearing equal to the term, and a repeat in another case. Compare the screen with the answer, not with the typed lines.
