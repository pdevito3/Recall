---
name: recall-import
description: Imports the entries of another journal, such as Notion, Day One, or text files, into Recall over MCP. Use when the person asks to import, move, or copy an old journal into Recall.
---

# Import a journal into Recall

You split the source into entries and put them in an import batch. The person reviews the batch in Recall and accepts it. Only an accepted batch goes into the journal.

## The tools

- A batch can be large. So always give `from` and `to` to `list_import_entries`.
- Use `remove_import_entries` only for an entry that you put by mistake, and only after the person says yes.
- Leave `accept_import_batch` to the person.

If you do not see `open_import_batch` or `put_import_entries`, your token cannot change the journal. A permission error on a put means the same. Then tell the person to connect you with a personal access token that has **Change my journal**. Put nothing until then.

## Steps

1. Get samples of the source. Read some days of each year, from the start to the end. The format can change from year to year.
2. Find the source word of the IDs. Call `list_import_batches`.
   - If an open batch has the name "Rules check, discard me", call `discard_import_batch` on it. It is a check batch of an earlier run.
   - If an open batch imported this same source, read its word from its IDs. To get them, call `list_import_entries` with `from` and `to` set to one date of the source. If the date gives no entry, try another date.
   - If an accepted batch imported this same source, you cannot read its IDs. Use the word that the name of the batch suggests, such as `notion` for "Notion 2019 to 2023". The check of step 6 checks the word.
   - If the name of a batch does not show its source, ask the person whether it holds this source. If it does, ask for its word.
   - Else pick a short lowercase word, such as `notion`. For a different source, such as a second workspace, pick a new word, such as `notion-work`.
3. Propose the split rules to the person. Give these items:
   - How you find a day and its date.
   - How you split a day into entries.
   - How you find a time, and which time zone you use.
   - The source word.
   - Three entries as you will put them, with their date, position, time, Moods, and Topics.
   - An entry with no time starts at 12:00 plus one minute for each position. So it can show before an entry of the same day that has a time.
4. Ask the person about each date or time that you cannot read for certain.
   - If the source has no dates, ask for the dates.
   - Use the time of a file only when the person says so.
   - If the text gives an hour with no am or pm, such as "at 6", ask. Use the answer for each such time.

   Then wait for a yes to the rules. If the person changes a rule, propose the rules again.
5. Call `list_journal_days` over the dates of the source. If the journal has none of these days, go to step 7. Else tell the person these items:
   - Each day that the journal has, with its count of Sessions.
   - With yes, Recall skips each entry whose ID is in the journal.
   - Each other entry goes next to the Sessions of its day.
   - An entry that the person rejected in an earlier batch comes back for the review.

   Then ask whether to import those days, and wait for the answer. With no, leave those days out of the batch.
6. If an accepted batch of this source exists, check the rules before you get the batch. A put never removes an entry. So another word or other rules can bring the same text into the journal two times.
   - Pick a day of step 5 whose count of Sessions is your count of entries. Else pick the day with the most Sessions.
   - Pick such a day also after a no in step 5, because the check batch puts nothing into the journal.
   - Open a check batch with the name "Rules check, discard me" and the zone of the source. Put the entries of that day in it.
   - Then call `discard_import_batch` on the check batch at once. It holds no review of the person, so you need no yes.
   - If no entry gives `InJournal` or `Deleted`, the source word is wrong. Ask the person for the word, and check again.
   - A difference is a day of step 5 whose count of Sessions is not your count of entries.
   - An `Added` entry of the check at a position below that count is also a difference.
   - If you find a difference, tell the person each one. Name the causes: a reject in an earlier batch, a deleted Session, a Session made in Recall, or other split rules.
   - Then ask whether to go on, and wait for the answer.
7. Get the batch.
   - If an open batch already holds this source, use it.
   - If the source has more than 20,000 entries, open one batch for each year. One accept takes at most 20,000 entries. Do steps 8 to 13 for each batch.
   - Else open one batch with `open_import_batch`. Name it for the source and the years, for example "Notion 2019 to 2023". Use a name that no other batch has. If Recall refuses the time zone, ask the person for another zone.
   - If you lose the result of `open_import_batch`, call `list_import_batches`. If it shows a batch with that name, use it. Else open the batch again.
8. If the batch held entries of this source before this run, check a sample of them against your rules.
   - Check the dates of the three entries of step 3. Also check the date of the source with the most entries.
   - Read one date in each call: give `list_import_entries` that date as `from` and `to`.
   - For each entry of such a date, your rules must give the same `clientSessionId`, `position`, `time`, and `raw`. Your rules must give no other entry on that date.
   - If the check fails, do the steps of "If the split rules change".
9. Do a test on a small part. Put the entries of three to five days, with one day of many entries if the source has one. If the source has five days or fewer, the test is the whole source.
10. Read the test back with `list_import_entries`. Compare each entry with the source. `startsAt` is in UTC, so convert it to the zone of the entry before you compare it.
11. Tell the person to open Imports in Recall, choose the batch, and check the test entries. Wait for a yes.
12. Put the full batch, in calls of at most 500 entries and at most 16 MB. The test entries give `Unchanged`, or `Rejected` when the person rejected them. Check the outcome of each entry.
13. After the last put, call `list_import_batches`. Count your entries whose last outcome is `Added`, `Changed`, `Unchanged`, or `Rejected`.
    - The entry count of the batch must be the same as your count.
    - If the batch has more entries, it holds entries of other rules. Put nothing more, and do the steps of "If the split rules change".
14. Tell the person the count of each outcome. Name each `InOtherBatch` entry and each entry that you could not correct. Tell the person to review the batch in Imports in Recall and accept it there.

### If the split rules change

Do these steps in each of these cases:

- The rules change after a put into an open batch, also after step 12.
- The check of step 8 fails.
- The count of step 13 is too large.

1. If the person changed the rules, propose the new rules, and wait for a yes.
2. Tell the person that the batch holds entries of other rules, so you must discard it. Say that a discard deletes each entry of the batch, and each reject and edit of the person. Wait for a yes.
3. Call `discard_import_batch`.
4. Start again at step 5 with the new rules.

## The fields of an entry

- `clientSessionId`: `<source>-<YYYY-MM-DD>-<position>`, with the source word of step 2, for example `notion-2021-03-03-1`. So the same source entry gets the same ID on each run. Never make it random. It has at most 100 characters. Send each ID once in a call.
- `date`: the day of the source as `YYYY-MM-DD`.
- `position`: the order of the entry in its day, from 0 to 719. Two entries of one date never share a position.
- `time`: the clock time at which the entry happens, as `HH:mm` on a 24-hour clock. So "at 9pm" gives `21:00`, and "Up at 5am" gives `05:00`. A time stamp also gives a time. A time that ends something, such as "until midnight", is not the time of the entry. If the text gives more than one such time, use the first. If the text gives no such time, leave it out.
- `timeZone`: leave it out, so that the entry takes the zone of the batch. Give an IANA zone only when the source shows that the person wrote the entry in another zone.
- `raw`: the words of the person as Markdown, exactly as the source has them. Keep each word, line break, list, and emphasis. Leave out only the structure of the source. That is the page title, the date title, a divider, and the indent of a toggle body. Also leave out the blank lines at the start and the end of an entry. Keep two spaces at the end of a line, because they are a line break in Markdown. Never fix, summarize, translate, or reword the text.
- `moods`: at most three Moods, or none. Give a Mood only when the text names it or shows it clearly. The person cannot change Moods in the review. Use only these Moods: Joyful, Content, Calm, Neutral, Tired, Anxious, Sad, Angry, Excited, Grateful.
- `topics`: one to five short Topics of the entry in title case, such as "Work" or "Running". Do not make a Topic of the name of a person. A Cleanup in Recall finds the People of an entry.

## The outcome of each entry

- `Added`, `Changed`, or `Unchanged`: the batch holds the entry.
- `Rejected`: the person rejected the entry or its day in the review. Leave it.
- `Invalid`: the field and the reason say what is wrong. Correct the entry and put it again.
- `PositionTaken`: another entry of that date has the position. `positionTakenBy` names it. If you did not put that entry in this run, the batch holds entries of other rules. Do the steps of "If the split rules change". Else correct the positions, and put again.
- `InTheFuture`: the entry starts after now. If it is an entry of today with no time, ask the person for its time. Else the date or the time is wrong, so read the source again.
- `InJournal`: the journal already has this entry. Skip it.
- `Deleted`: the person deleted this entry from the journal. Skip it, and keep its ID.
- `InOtherBatch`: another open batch holds this ID. To find the batch, call `list_import_entries` with the date of the entry on each open batch of `list_import_batches`. Tell the person which batch holds it.

If a put or a discard fails with no outcomes, read the error.

- If the error says "This batch is in your journal, so it cannot change.", the person accepted the batch. Stop, and tell the person.
- If the error says "This import batch does not exist.", the person discarded the batch. Stop, and tell the person.
- If the error says nothing that you can correct, or you lose the result, send the same put again. A put of the same entries again changes nothing.
- If the same put fails two times with no result, send half of its entries in each call.

## Worked example: Notion

A Notion export has these shapes. One workspace can have each of them, in different years.

- **A toggle for each day.** The title of the toggle is the date. In a Markdown export, the toggle is a list item `- <title>` at the left edge, with its body indented below it. Or it is a `<details>` block with the title in `<summary>`. The body is one entry. A list item inside the body is text of the entry, not a day.
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
