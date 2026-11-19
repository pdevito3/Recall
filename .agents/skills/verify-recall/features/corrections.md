# Corrections

A person keeps the words that dictation gets wrong at `/corrections`: each term, the ways dictation mishears it, and whether Cleanup replaces each mishearing every time. A term with no mishearings is a known word, and the section `Known words` above the list holds it as a pill. The iPhone app edits the same Corrections, and each device shows the changes of the other.

## Sub-features

- `known words` shows each Correction with no mishearings as a pill in the list `Your known words`, in one field with the input `Known words` (placeholder `Add a word and press Enter`). Enter saves the trimmed word with no mishearings and `hardReplace` false, clears the input, and keeps the focus in it. A word that is already a Correction, in any case, sends nothing and shows `“<term>” is already a known word.` or `“<term>” is already in your corrections below. Edit it there.` A refusal of the Server shows its message under the field, or `The word was not saved. Try again.` when it has none. The button `Remove <term>` of a pill deletes it with no confirm and puts the focus in the input. Cleanup gets each known word alone.
- `list` shows `Your corrections` under `Misheard words`: each term with mishearings, with the line `Heard as <mishearings>` and `Always replaced` when the flag is on. A known word is not in it.
- `empty` shows `You have no misheard words yet. Add a word that dictation gets wrong.`.
- `search` finds a term or a mishearing with no regard to case or accents. The search is in the URL. No match shows `No misheard word has “<search>” in its term or its mishearings.`.
- `add` opens the dialog `Add a correction` from `Add correction`, with `Term`, `Mishearings` (one on each line, at least one that differs from the term in more than case), and `Always replace`. `Save correction` closes it and shows the toast `Correction saved`.
- `edit` opens `Edit <term>` from the button `Edit <term>` with the saved values. An edit can clear the mishearings: the save makes the term a known word, shows the toast `“<term>” is now a known word.`, and puts the focus on the heading. A save after a save on another device shows `This correction changed after you opened it.` and `Load the latest`.
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
- **Search.** Fill `Search misheard words` with a mishearing in other case. The row stays, and the URL has the search. A search with no match shows the no-match text.
- **Edit.** Choose `Edit <term>`. Add a line to `Mishearings`, clear `Always replace`, and choose `Save correction`. The `PUT` body has the version of the row, and the row shows the mishearings of the answer with no `Always replaced`.
- **Delete.** Choose `Delete <term>`, then `Delete correction`. The list shows the empty text, and the heading has the focus.
- **Known words.** Type `Radancy`, `Profisee`, and `Aspire` in `Known words`, each with Enter. Each `POST` body has no mishearings and `hardReplace` false, each pill shows the term of the answer, and the list still shows the empty text. Type the first term in lower case and press Enter: the alert says that it is already a known word, and no `POST` goes out. Choose `Remove <second term>`: the `DELETE` has the version of the pill, two pills stay, and the input has the focus.
- **Edit to a known word.** Add `GRDB` with the mishearing `grdb`: the dialog says `A mishearing must differ from the term.` and sends nothing. Change it to `grid b` and save. Choose `Edit <term>`, clear `Mishearings`, and save. The `PUT` body has no mishearings, the toast says that the term is now a known word, the term is a pill, the list shows the empty text, and the heading has the focus.
- **Proof.** Run `node $S/scripts/browse.mjs $S/recipes/corrections.mjs`. The captures `empty`, `added`, `searched`, `edit-dialog`, `edited`, `deleted`, `known-words`, `known-word-in-use`, `known-word-removed`, `mishearing-is-the-term`, `edit-clear-mishearings`, `edited-to-known-word`, `known-words-phone`, and `known-words-phone-dark` show each state. `KnownWordCleanupTests` proves that the Cleanup prompt lists a known word, because a browser run cannot see the prompt.

## Gotchas

- The sweep gives `canonicalTerm` a long value in each answer, so the recipe finds a row by the term of the answer, not by the typed term.
- The Server drops a blank mishearing, a mishearing equal to the term, and a repeat in another case. Compare the screen with the answer, not with the typed lines.
