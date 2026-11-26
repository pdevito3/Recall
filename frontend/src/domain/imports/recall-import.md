---
name: recall-import
description: Imports the entries of another journal (Notion, Day One, text files, or any other source) into Recall through the Recall MCP tools. Use when the person asks to import, move, or copy an old journal into Recall.
---

# Import a journal into Recall

You read the source, split it into entries, and write the entries into an import batch in Recall. The person reviews the batch in Recall and accepts it. Only an accepted batch goes into the journal. Recall does not parse the source. You find the rules of its format, and the person approves them.

## The tools

- `list_journal_days` gives the days that the journal already has, for at most 366 days in one call.
- `open_import_batch` opens a batch with a name and the IANA time zone of the source.
- `put_import_entries` adds or changes at most 500 entries of a batch in one call.
- `list_import_entries` gives the entries of a batch as Recall stores them, with the start time of each.
- `remove_import_entries` removes entries of a batch by their `clientSessionId`.
- `list_import_batches` gives each batch, with its state and its entry count.
- `discard_import_batch` deletes an open batch and each of its entries.
- `accept_import_batch` moves a batch into the journal. Leave the accept to the person.

If you see none of these tools, tell the person to connect you to Recall with a personal access token that can change the journal.

## Steps

1. Get samples of the source. Read some days from the start, the middle, and the end. If the source covers many years, read some days of each year, because the format can change from year to year.
2. Propose the split rules to the person. Say how you find a day and its date, how you split a day into entries, how you find a time, and which time zone you use. Show three entries as you will write them, with their date, position, time, Moods, and Topics. Ask the person about each date or time that you cannot read for certain.
3. Wait for a yes. If the person changes a rule, propose the rules again.
4. Call `list_journal_days` over the dates of the source. If the journal already has some of these days, tell the person before you write.
5. Open one batch with `open_import_batch`. Name it for the source and the years, for example "Notion 2019 to 2023". If the source has more than 20,000 entries, open one batch for each year, because one accept takes at most 20,000 entries.
6. Do a test on a small part. Put the entries of three to five days, with one day of many entries if the source has one.
7. Read the test back with `list_import_entries`. Compare each entry with the source.
8. Tell the person to open Imports in Recall, choose the batch, and check the test entries. Wait for a yes.
9. If the person asks for other split rules, call `discard_import_batch`, change the rules, and go back to step 5.
10. Write the full batch. Put the rest of the entries in calls of at most 500 entries and at most 16 MB. Check the outcome of each entry.
11. Tell the person how many entries you wrote and which outcomes need them. Tell them to review the batch in Imports in Recall and accept it there.

## The fields of an entry

- `clientSessionId`: an ID that is the same for the same source entry on each run, of at most 100 characters. Make it from the source, the date, and the position, for example `notion-2021-03-03-1`. Never make it random.
- `date`: the day of the source as `YYYY-MM-DD`.
- `position`: the order of the entry in its day, from 0. Two entries of one date never share a position. A day holds at most 720 entries, positions 0 to 719.
- `time`: the time as `HH:mm` on a 24-hour clock. Give it only when the text gives a time, such as "at 9pm" (`21:00`) or a time stamp. Otherwise leave it out, and Recall starts the entry at 12:00 plus one minute for each position.
- `timeZone`: leave it out, so that the entry takes the zone of the batch. Give an IANA zone only when the source shows that the person wrote the entry in another zone.
- `raw`: the words of the person as Markdown, exactly as the source has them. Keep each word, line break, list, and emphasis. Leave out only the structure of the source: the date title and the divider. Never fix, summarize, translate, or reword the text.
- `moods`: at most three Moods that the text shows, or none. Use only these Moods: Joyful, Content, Calm, Neutral, Tired, Anxious, Sad, Angry, Excited, Grateful.
- `topics`: one to five short Topics of the entry in title case, such as "Work" or "Running". Each Topic has at most 100 characters.

## The outcome of each entry

- `Added`, `Changed`, or `Unchanged`: the batch holds the entry. Go on.
- `Rejected`: the person rejected the entry in the review. Leave it.
- `Invalid`: the field and the reason say what is wrong. Correct the entry and put it again.
- `PositionTaken`: another entry of that date has the position. `positionTakenBy` names it. Correct the positions and put again.
- `InTheFuture`: the date or the time is wrong. Read the source again.
- `InJournal`: the journal already has this entry. Skip it.
- `Deleted`: the person deleted this entry from the journal. Skip it, and keep its ID.
- `InOtherBatch`: another open batch holds this ID. Tell the person, and find the batch with `list_import_batches`.

If a call fails with no outcomes, read the error. Send each `clientSessionId` once in a call, and send fewer entries in each call.

A put of the same entries again changes nothing. If a call fails or you lose the result, send the same call again.

## Worked example: Notion

A Notion export has these shapes. One workspace can have each of them, in different years.

- **A toggle for each day.** The title of the toggle is the date. In a Markdown export, the toggle is a list item `- <title>` with its body indented below it, or a `<details>` block with the title in `<summary>`. The body is one entry.
- **A toggle with dividers.** A divider is a line `---` in the body. Each part between dividers is its own entry, in the order of the source. An empty part gives no entry.
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

Recall starts these entries at 12:00, 21:00, 12:02, and 12:00 on their days. The second entry can have the Mood Calm and the Topic "Walks".
