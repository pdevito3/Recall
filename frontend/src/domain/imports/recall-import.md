---
name: recall-import
description: Imports the entries of another journal (Notion, Day One, text files, or any other source) into Recall through the Recall MCP tools. Use when the person asks to import, move, or copy an old journal into Recall.
---

# Import a journal into Recall

You read the source, split it into entries, and write the entries into an import batch in Recall. The person reviews the batch in Recall and accepts it. Only an accepted batch goes into the journal. You find the rules of its format, and the person approves them.

## The tools

- `list_journal_days` gives the days that the journal already has, for at most 366 days in one call.
- `open_import_batch` opens a batch with a name and the IANA time zone of the source.
- `put_import_entries` adds or changes at most 500 entries of a batch in one call.
- `list_import_entries` gives the entries of a batch as Recall stores them, with the start time of each. To get only some dates, give `from` and `to`.
- `remove_import_entries` removes entries of a batch by their `clientSessionId`. Use it only for an entry that you put by mistake, such as a page that is not part of the journal, and only after the person says yes.
- `list_import_batches` gives each batch with its state.
- `discard_import_batch` deletes an open batch and each of its entries.
- `accept_import_batch` moves a batch into the journal. Leave the accept to the person.

If you do not see `open_import_batch` and `put_import_entries`, or a put gives a permission error, tell the person to connect you to Recall with a personal access token that has **Change my journal**. Put nothing until then. If `accept_import_batch` gives a permission error, the import review is on, and only the person can accept.

## Steps

1. Get samples of the source. Read some days from the start, the middle, and the end, and some days of each year, because the format can change from year to year.
2. Find the source word of the IDs. Call `list_import_batches`.
   - If an earlier batch imported this same source, use its word again. If that batch is open, read the word from the IDs of `list_import_entries`. If it is accepted, you cannot read its IDs, so use the best word, and step 7 checks it.
   - Else pick a short lowercase word, such as `notion`. For a different source, such as a second workspace, pick a word that no earlier import used, such as `notion-work`.
3. Propose the split rules to the person. Say how you find a day and its date, how you split a day into entries, how you find a time, which time zone you use, and the source word. Show three entries as you will put them, with their date, position, time, Moods, and Topics.
   - Ask the person about each date or time that you cannot read for certain. If the source has no dates, ask the person for the dates. Use the time of a file only when the person says so.
   - If the text gives an hour with no am or pm, such as "at 6", ask the person. Use the answer for each such time.
   - Tell the person that an entry with no time starts at 12:00 plus one minute for each position. So it can show before an entry of the same day that has a time.
4. Wait for a yes. If the person changes a rule, propose the rules again.
5. Call `list_journal_days` over the dates of the source. If the journal already has some of these days, tell the person which days, and how many Sessions each day has. Say that an earlier import can be among them. Ask whether to import those days, and wait for the answer. Tell the person that with yes, Recall skips each entry whose ID is in the journal, and puts each other entry next to the Sessions of its day. With no, leave those days out of the batch.
6. Get the batch.
   - If an open batch already holds this source, use it.
   - If the source has more than 20,000 entries, open one batch for each year, because one accept takes at most 20,000 entries. Do steps 7 to 11 for each batch.
   - Else open one batch with `open_import_batch`. Name it for the source and the years, for example "Notion 2019 to 2023". If Recall refuses the time zone, ask the person for another zone.
   - If you lose the result of `open_import_batch`, call `list_import_batches`. If it shows a batch with that name, use it. Else open the batch again.
7. Do a test on a small part. Put the entries of three to five days, with one day of many entries if the source has one. If the source has five days or fewer, the test is the whole source. If you use the word of an accepted batch, also put a day of step 5 that the journal already has. If no entry of that day gives `InJournal`, the word is wrong. Tell the person, and ask for the word before you put more.
8. Read the test back with `list_import_entries`. Compare each entry with the source. `startsAt` is in UTC, so convert it to the zone of the entry before you compare it.
9. Tell the person to open Imports in Recall, choose the batch, and check the test entries. Wait for a yes.
10. Put the full batch, in calls of at most 500 entries and at most 16 MB. The test entries give `Unchanged`, or `Rejected` when the person rejected them. Check the outcome of each entry.
11. Tell the person the count of each outcome. Name each `InOtherBatch` entry and each entry that you could not correct. Tell them to review the batch in Imports in Recall and accept it there.

If the split rules change after a put, also after step 10, propose the new rules. Tell the person that you must discard the batch, and that a discard deletes each entry of the batch and each reject and edit of the person. Wait for a yes. Then call `discard_import_batch`, and start again at step 6 with the new rules.

## The fields of an entry

- `clientSessionId`: `<source>-<YYYY-MM-DD>-<position>`, with the source word of step 2, for example `notion-2021-03-03-1`. So the same source entry gets the same ID on each run. Never make it random. It has at most 100 characters. Send each ID once in a call.
- `date`: the day of the source as `YYYY-MM-DD`.
- `position`: the order of the entry in its day, from 0 to 719. Two entries of one date never share a position.
- `time`: the clock time at which the entry happens, as `HH:mm` on a 24-hour clock, such as "at 9pm" (`21:00`), "Up at 5am" (`05:00`), or a time stamp. A time that ends something, such as "until midnight", is not the time of the entry. If the text gives more than one time at which the entry happens, use the first. If the text gives no such time, leave it out, and Recall starts the entry at 12:00 plus one minute for each position.
- `timeZone`: leave it out, so that the entry takes the zone of the batch. Give an IANA zone only when the source shows that the person wrote the entry in another zone.
- `raw`: the words of the person as Markdown, exactly as the source has them. Keep each word, line break, list, and emphasis. Leave out only the structure of the source, such as the page title, the date title, a divider, the indent of a toggle body, and the blank lines at the start and the end of an entry. Keep two spaces at the end of a line, because they are a line break in Markdown. Never fix, summarize, translate, or reword the text.
- `moods`: at most three Moods, or none. Give a Mood only when the text names it or shows it clearly, because the person cannot change Moods in the review. Use only these Moods: Joyful, Content, Calm, Neutral, Tired, Anxious, Sad, Angry, Excited, Grateful.
- `topics`: one to five short Topics of the entry in title case, such as "Work" or "Running". Do not make a Topic of the name of a person. Recall finds People on its own.

## The outcome of each entry

- `Added`, `Changed`, or `Unchanged`: the batch holds the entry.
- `Rejected`: the person rejected the entry or its day in the review. Leave it.
- `Invalid`: the field and the reason say what is wrong. Correct the entry and put it again.
- `PositionTaken`: another entry of that date has the position. `positionTakenBy` names it. Correct the positions and put again.
- `InTheFuture`: the entry starts after now. If it is an entry of today with no time, put it again after its start, or ask the person for its time. Else the date or the time is wrong, so read the source again.
- `InJournal`: the journal already has this entry. Skip it. If you did not expect `InJournal`, tell the person, because an earlier import can have used the same source word.
- `Deleted`: the person deleted this entry from the journal. Skip it, and keep its ID.
- `InOtherBatch`: another open batch holds this ID. To find the batch, call `list_import_entries` with the date of the entry on each open batch of `list_import_batches`. Tell the person which batch holds it.

If a put fails with no outcomes, read the error.

- If the error says "This batch is in your journal, so it cannot change.", the person accepted the batch. Stop, and tell the person.
- If the error says "This import batch does not exist.", the person discarded the batch. Stop, and tell the person.
- Else do what the error says. If it says nothing that you can correct, or you lose the result, send the same put again. A put of the same entries again changes nothing. If the same put fails two times with no result, send half of its entries in each call.

## Worked example: Notion

A Notion export has these shapes. One workspace can have each of them, in different years.

- **A toggle for each day.** The title of the toggle is the date. In a Markdown export, the toggle is a list item `- <title>` at the left edge, with its body indented below it, or a `<details>` block with the title in `<summary>`. The body is one entry. A list item inside the body is text of the entry, not a day.
- **A toggle with dividers.** A divider is a line `---` in the body. Each part between dividers is its own entry. An empty part gives no entry.
- **A page or a heading for each day.** The title of the page or the heading is the date. The body is the day, and dividers split it as in a toggle.

This export has two days:

```markdown
- March 3, 2021
    Slept late. Pancakes with Ana.

    ---

    At 9pm we walked to the lake. I felt **calm**.

    ---

    Read until midnight.
- March 4, 2021
    Long day at work.
```

It gives four entries in a batch with the zone `America/Chicago`:

| `clientSessionId` | `date` | `position` | `time` | `raw` |
|---|---|---|---|---|
| `notion-2021-03-03-0` | `2021-03-03` | 0 | | `Slept late. Pancakes with Ana.` |
| `notion-2021-03-03-1` | `2021-03-03` | 1 | `21:00` | `At 9pm we walked to the lake. I felt **calm**.` |
| `notion-2021-03-03-2` | `2021-03-03` | 2 | | `Read until midnight.` |
| `notion-2021-03-04-0` | `2021-03-04` | 0 | | `Long day at work.` |

Recall starts these entries at 12:00, 21:00, 12:02, and 12:00 on their days. The second entry can have the Mood Calm.
