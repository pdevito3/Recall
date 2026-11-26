# Import a journal with an agent

An AI agent can move the entries of another journal into Recall. The agent reads the source, such as a Notion export, and splits it into entries. It writes the entries into an import batch over MCP. You review the batch in Recall and accept it. Only an accepted batch goes into your journal. Recall does not parse a source, so the agent can read any format.

The rules have IDs in [the imports spec](specs/imports.md). [ADR 0026](adr/0026-an-import-batch-is-a-draft-area-outside-the-journal.md) gives the reasons for the design.

## Connect an agent

### Make a token

1. In Recall, open **Settings** > **Personal access tokens**, and choose **New token**.
2. Give the token a name, such as "Import agent".
3. Select **Change my journal**. A token that changes the journal also reads it. The import needs no other permission.
4. Pick an expiry, and choose **Create token**.
5. Copy the token. Recall shows it only one time.

A token with only **Read my journal** sees only the list tools. It gets an error on each write. A token with no journal access sees no import tool.

### Add Recall to the agent

The MCP URL is the address of your Recall with `/mcp` at the end, for example `https://recall.example.com/mcp`. **Imports** in Recall shows this URL, with **Copy MCP URL**. The agent sends the token in the header `Authorization: Bearer <token>`.

The examples below read the token from the environment variable `RECALL_PAT`. Set it before you start the agent, for example `export RECALL_PAT=rcl_pat_...`. A token that you type in a command goes into the history of your shell, so the variable is safer.

For Claude Code, run this command with your MCP URL:

```bash
claude mcp add --transport http recall https://recall.example.com/mcp --header "Authorization: Bearer $RECALL_PAT"
```

**Imports** in Recall shows the same command with your MCP URL and with `<your token>` in place of `$RECALL_PAT`, with **Copy command**. By default, `claude mcp add` adds Recall only to the project of the current directory. To add it to all your projects, put `--scope user` after `add`.

For Claude Code with a `.mcp.json` file at the root of the project:

```json
{
  "mcpServers": {
    "recall": {
      "type": "http",
      "url": "https://recall.example.com/mcp",
      "headers": { "Authorization": "Bearer ${RECALL_PAT}" }
    }
  }
}
```

Claude Desktop starts a local MCP server for each entry of its config file. Use a bridge, such as `mcp-remote`, that sends the header to `/mcp`. The bridge needs Node.js 18 or later, which also gives `npx`.

1. In Claude Desktop, open **Settings** > **Developer**, and choose **Edit Config**. This opens the folder of `claude_desktop_config.json`.
2. Add the entry below to the file, with your MCP URL and your token.
3. Quit Claude Desktop, and start it again.

```json
{
  "mcpServers": {
    "recall": {
      "command": "npx",
      "args": ["-y", "mcp-remote", "https://recall.example.com/mcp", "--header", "Authorization:${RECALL_AUTH}"],
      "env": { "RECALL_AUTH": "Bearer rcl_pat_..." }
    }
  }
}
```

If your MCP URL starts with `http://`, put `"--allow-http"` after the URL in `args`. Use plain HTTP only on a private network, because another device on the network can read the token. To turn on HTTPS, see [install.md](install.md#turn-on-https).

Connect another MCP client with the Streamable HTTP transport. The server keeps no MCP session, so each request stands alone. Send JSON-RPC with `POST`, and accept both JSON and an event stream:

```bash
curl https://recall.example.com/mcp \
  -H "Authorization: Bearer $RECALL_PAT" \
  -H "Content-Type: application/json" \
  -H "Accept: application/json, text/event-stream" \
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/list"}'
```

The response is an event stream. The JSON-RPC result is on the `data:` line. Call a tool with the method `tools/call` and `params` of `{ "name": "<tool>", "arguments": { ... } }`. The other auth rules of a token are in [authentication.md](authentication.md#personal-access-tokens).

If the agent cannot connect, the token is wrong, expired, or revoked. Make a new token.

### Give the agent the skill

Open **Imports** in Recall. The section **Import with an agent**, below the list of batches, has the `recall-import` skill. Choose **Copy skill**, and give the text to your agent. To read the skill first, open **The recall-import skill**. For Claude Code, save it as `.claude/skills/recall-import/SKILL.md` in your project, or as `~/.claude/skills/recall-import/SKILL.md` for all your projects. For another agent, save it as a skill of the agent, or paste it at the start of the chat. The file of the skill is [`frontend/src/domain/imports/recall-import.md`](../frontend/src/domain/imports/recall-import.md).

### Start the import

Ask the agent to import your journal, and give it the folder of the export. The agent proposes its split rules and a test before it puts all the entries. Claude Desktop reads only the files that you attach to the chat. For a large export, use Claude Code, or add a file-system MCP server to Claude Desktop.

## The tools

| Tool | What it does | Access |
|---|---|---|
| `list_journal_days` | Gives each day in a range that already has a Session, with the count of its Sessions. The range is at most 366 days. | Read |
| `open_import_batch` | Opens a batch with a name of 1 to 200 characters and an IANA time zone, such as `America/Chicago`. | Change |
| `list_import_batches` | Gives each batch, newest first, with its state, its entry count, and its version. | Read |
| `put_import_entries` | Adds or changes at most 500 entries of an open batch, by `clientSessionId`. Each entry gets an outcome. | Change |
| `list_import_entries` | Gives the entries of a batch in the order of date and position, with an optional range of dates. Each entry has its `raw` as the agent sent it, the review of the person, and `startsAt`. | Read |
| `remove_import_entries` | Removes entries of an open batch by `clientSessionId`. Each ID gets `Removed`, `NotFound`, or `Rejected`. | Change |
| `discard_import_batch` | Deletes an open batch and each of its entries. | Change |
| `accept_import_batch` | Makes a Session of each kept entry. With the import review on, only you can accept, in Recall in a browser. | Change |

## The rules for an agent

### An entry

| Field | Rule |
|---|---|
| `clientSessionId` | 1 to 100 characters. Make it stable: the same source entry gets the same ID on each run, for example `notion-2021-03-03-1`. |
| `date` | The day of the source, as `YYYY-MM-DD`, from 1000-01-01 to 9998-12-31. |
| `position` | The order of the entry in its day, from 0 to 719. Two entries of one date in a batch cannot have the same position. |
| `time` | `HH:mm`, only when the text gives a time. |
| `timeZone` | An IANA zone, or leave it out for the zone of the batch. |
| `raw` | The words of the person as Markdown, at most 1,000,000 characters. |
| `moods` | At most 50 values of at most 100 characters. The skill tells the agent to use only the Moods that Recall knows. |
| `topics` | At most 50 values of at most 100 characters. |

### The start time

An entry with a `time` starts at that time on its date. An entry with no `time` starts at 12:00 plus one minute for each position. So entries at positions 0, 1 (at 9pm), and 2 start at 12:00, 21:00, and 12:02. Recall computes the start time. An edit, a reject, or a put never moves the start time of another entry.

The zone of an entry is its own `timeZone`, else the zone of the batch. The Fixed zone of your settings does not change it, because the day comes from the date of the source.

### Raw is Markdown

The agent sends the words of the source with no change: no fixes, no summary, no new words. Raw keeps the line breaks, lists, and emphasis of the source. The accept turns the Markdown into the text of the Session and keeps each word. A later Cleanup writes Cleaned and never changes Raw.

### The limits of a call

- A put takes at most 500 entries, and each `clientSessionId` once. A larger call gets an error and stores nothing.
- A call to `/mcp` has at most 16 MB. A larger call gets 413. Send fewer entries in each call.
- One accept takes at most 20,000 kept entries. For a larger import, open one batch for each year.

### The outcome of a put

Each entry of a put gets the first outcome that applies.

| Outcome | What it means | What the agent does |
|---|---|---|
| `Invalid` | A field is not valid. `field` and `reason` say which and why. | Correct the entry and put it again. |
| `InJournal` | A Session of the journal has this ID. | Skip it. |
| `Deleted` | You deleted the Session of this ID. | Skip it. |
| `InOtherBatch` | Another open batch holds this ID. | Tell you. Find the batch with `list_import_entries` on each open batch. |
| `InTheFuture` | The start time is in the future. An entry of today with no time starts at 12:00 plus its position, so before that time it gets this outcome. | For an entry of today with no time, put it again after that time, or ask you for its time. For another entry, read the source again for the date and the time. |
| `PositionTaken` | Another entry of the date has the position. `positionTakenBy` names it. | Correct the positions and put again. |
| `Rejected` | You rejected the entry or its day. The put stored the new words, and the reject stays. If the put moved the entry off a rejected day, it stored nothing. | Leave it. |
| `Unchanged` | The batch already holds this entry. | Go on. |
| `Changed` | The put changed the entry. | Go on. |
| `Added` | The put added the entry. | Go on. |

`Changed` and `Added` write the entry. `Rejected` writes the entry, but not when the put moves the entry off a rejected day. The other outcomes write nothing.

### A put again is safe

A put of the same call again changes nothing. So the agent can send a put again after an error or a lost result. A call of `open_import_batch` again opens a second batch. So after a lost result of `open_import_batch`, the agent finds the batch with `list_import_batches`. A second run of a whole import also stores only the new entries, because each entry that is already in the journal gets `InJournal`. An entry that you rejected in an accepted or a discarded batch is not in the journal, so it gets `Added` again. Reject it again in the review. A put never changes your review, and it removes no entry.

### The review gate

The import review is on by default. With the review on, only you accept a batch, in Recall in a browser. `accept_import_batch` then gives an error, and the agent tells you to accept in Recall. To let an agent accept its own batch, clear **Only I can accept an import batch** in **Settings**. You can change this setting only in Recall in a browser.

## Review and accept

1. Open **Imports** in Recall, and choose the batch.
2. Read the entries of each day. Each entry shows its start time, its words, its Moods, and its Topics.
3. If an entry has wrong words or a wrong time, choose **Edit**, change it, and choose **Save**. Your edit wins at the accept, also when the agent puts the entry again.
4. If an entry does not belong in your journal, choose **Reject**. To reject all the entries of a day, choose **Reject day**.
5. Choose **Accept**. The dialog gives the count of the kept entries and of the rejected entries. Choose **Accept batch**.

The accept makes one Session of each kept entry, in one save. The page then shows the counts of the result, with a link to your journal. A Summary of an affected period reads Stale. The accept starts no Cleanup.

To drop a batch with none of its entries, choose **Discard** in the list of batches or on the page of the batch. A discard cannot be undone. After an accept or a discard, a rejected ID is free for a later import.
