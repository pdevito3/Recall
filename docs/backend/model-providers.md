# Model providers

A Cleanup sends the words of a Session to a model provider. The address of the provider comes from an Admin, and the answer comes from a service that the app does not control. This page gives the rules that hold for each call. Each rule has a test in `ProviderLimitTests`, `ProviderTextAndAddressTests`, or `ModelProviderClientTests`.

## The active provider

A connect does not change which provider a Cleanup uses. A provider is active only when a person chooses it and it has a model.

- A new provider has no `ActivatedOn`. Connect it, test it, pick a model, and then choose **Make active**.
- A choice of a provider with no model gets 422.
- A provider that loses its model stops being active, and a person must choose it again.
- The newest choice of a tenant is the active provider. If that provider is deleted, the choice before it is active again.
- With no active provider, a Cleanup start gets 422, the page says why, and its button stays off.
- Each try of a run reads the active provider again. If an Admin deletes the active provider while a run waits to try again, the next try sends the words to the provider that was active before it. The Cleanup card reads the active provider every 2 s while a run lasts and names it, so the person sees where the words go. A person who closed the page does not see the change.

## Limits of a call

`ProviderLimits` holds each value. Each read on each adapter goes through `ProviderReadGuard`, which is a handler of the HTTP client. So the limits also hold for the stream of the Anthropic SDK, the model list, and each error body.

| Limit | Value | Why |
|---|---|---|
| Connect | 10 s | A host that answers on a network answers in this time. A person waits for the test button. |
| No progress on a read, also for the headers | 3 min | A local model can load and read a long prompt before its first byte. Anthropic sends `ping` events during a stream. A hung call ends here. |
| No words in a stream | 10 min | Only a word or a thought of the model counts. A `ping`, a space, or an empty delta does not. A model can think before it writes, and Anthropic sends only pings during a thinking block when the thoughts are not shown, so the limit must be longer than a long thought. No API reference gives an upper bound: Anthropic asks for a stream for a request that can take over 10 minutes, and OpenAI says that a reasoning model can take several minutes. Then a provider that sends only pings holds a slot of the queue for 10 min, and the run ends as `ProviderUnavailable` with no new try. |
| A whole call for words, and all tries of one run | 40 min | The longest answer is 64 000 tokens. At 30 tokens each second, this is about 36 min. A wait before the first claim does not count. A wait between tries and a stop of the server do count, so a run that passes the limit in a long stop ends with no new call, and a person can run it again. |
| A call for the model list | 30 s | A person waits for it, and no model runs. |
| A body that is not a stream | 16 MiB | A list of models or an error is far smaller. The read ends when the bytes pass the limit. |
| A stream | 64 MiB | 250 000 tokens with an envelope of up to 250 bytes for each. |
| One line of a stream | 4 MiB | The text of one event goes to memory as a whole. |
| The words of an answer | 2 x the limit of a Draft | The longest answer that can give a Draft, with an escape for each character. |
| Pages of the Anthropic model list | 10 pages of 1000, and 2000 models | `has_more` comes from the provider, so a list that never ends must stop. |

A hung provider ends a run in about 19 minutes: 6 tries of 3 minutes, and 62 s of waits between them. A provider that sends only pings ends a run in 10 minutes. A stream that sends words can run for the full 40 minutes.

Sources for the limit of no words, read on 7 November 2026: [Anthropic streaming](https://platform.claude.com/docs/en/build-with-claude/streaming), [Anthropic extended thinking](https://platform.claude.com/docs/en/build-with-claude/thinking), [Anthropic errors](https://platform.claude.com/docs/en/api/errors), and [OpenAI background mode](https://developers.openai.com/api/docs/guides/background).

## What a failure means

| Fault | Kind | What the run does |
|---|---|---|
| No answer, a cut connection, a read with no progress, a status of 408, 409, 429, or 5xx | `Unavailable` | Tries again, up to 6 calls. Then `ProviderUnavailable`. |
| Another status | `Refused` | Ends at once as `ProviderRefused`. |
| Invalid JSON, an HTML body with status 200, a bad charset, a body or a stream over its limit, an empty stream, a cut answer (`max_tokens` or `length`), a refusal | `BadOutput` | Ends at once as `BadModelOutput`. The same bytes come again on a retry. |
| A refused address | `Refused` | Ends at once as `ProviderRefused`. |
| Bytes that are not UTF-8 in a body with a 2xx status, on a stream or in a whole body | `BadOutput` | Ends at once as `BadModelOutput`, so no `U+FFFD` takes the place of a word. A model list with such bytes is a failure and not an empty list. |
| A stream with status 200 that closes with no event | `BadOutput` | Ends at once as `BadModelOutput` after 1 call, on each kind. |
| No word for the limit above | `NoWords` | Ends at once as `ProviderUnavailable`, with no new try. |

## The cost of an answer

An answer is Markdown, and the server turns it into the stored document. The time and the memory of Markdig grow faster than the length for some forms, so `MarkdownCost` checks each text before the conversion, on each path: the answer of a model provider, the result of a device, and each other caller of `MarkdownToProseMirror`.

| Bound | Value | Why |
|---|---|---|
| The length of an answer | The limit of a Draft | A longer text cannot be a Draft. The run ends as `BadModelOutput`, and a device result gets 422. |
| Marks: line ends, the backtick, and the signs `\|`, `*`, `_`, `~`, `=`, `+`, `[`, `]`, `<`, `&`, `#`, `!`, and `\` | 33 333, the limit of a Draft / 30 | Each mark can make a node, and each node takes at least 30 characters of the stored form. So a text with more marks cannot fit in a Draft. A table of 990 000 pipes took 1 GB before this bound. |
| The work of links: for each block between blank lines, the count of `](` and `]:` times the length of the block | 500 000 000 | Markdig reads to the end of the block for each open link, so `[a](` repeated takes time that grows with the square of the length: 371 s for 1 M characters. One unit took about 1.5 ns, so the bound is under 1 s. |
| Marks nested in marks | 64 levels | A deeper tree ended the process with a stack overflow, which no `catch` can stop. |
| Blocks of one line: the signs `>`, `-`, `+`, `*`, `1.`, and `1)` that open a quote or a list item at its start, and half of its indent and of the indent after each `>` | 64 | Markdig reads the rest of the line again for each new list item, and the indent again for each item that goes on. So `- ` repeated 300 000 times took 10 s, and lines that each go 32 items deeper took 12 s. A thematic break and indented code open no block, so they do not count. Only a new list item can start indented code: after a `>`, a list item of an earlier line can take the indent, so `> - a` and then `>     ` with `- ` repeated 300 000 times took 6 s when the bound stopped at that indent. The JSON writer refuses a depth over 64, and each block takes two levels, so a line in more than 32 blocks was stored as the source before this bound too. With the bound, each such form takes under 30 ms. |

On the release image with a limit of 1 GiB, each text that passes the bounds and is the worst of its form was stored, one at a time and four at the same time: 11 000 open links, 33 000 signs of emphasis, and a table of 8 300 rows. The peak was 604 MB, and the slowest took 8.2 s from the request to the answer. With a limit of 512 MiB, one at a time was stored, but four tables at the same time stopped the process with exit 137. So `docs/install.md` asks for 1 GiB.

A text over a bound is stored as the source: each line is a paragraph of plain text, and no word is lost. A conversion that throws `OutOfMemoryException`, a parse error, or a time-out of a regular expression also stores the source, with no new try. The rule of the words then compares the shown words with the source, and a form that would change them stays as the source too. A pair of emphasis signs between two letters, as in `a~~b~~c` or `2*3*4`, would join two words, so the conversion keeps those signs as text and the rest of the answer keeps its form.

The test button always answers with 200 and with one of these kinds. A person can also stop a run: see [The Cleanup job](background-jobs.md#the-cleanup-job).

## Addresses

A private address stays allowed, because a local model on a home network is a real use. `ProviderAddressRule` refuses:

| Rule | Addresses | Why |
|---|---|---|
| Link-local | `169.254.0.0/16`, `fe80::/10` | This range holds the metadata address of most clouds. |
| Named metadata addresses | `fd00:ec2::254`, `fd20:ce::254`, `100.100.100.200`, `168.63.129.16`, `192.0.0.192` | These cloud services are outside the link-local range. |
| Loopback, in a container | `127.0.0.0/8`, `::1`, `localhost` | In a container, a loopback address is the app itself, and a local model is on another address. Outside a container, for example in development, loopback stays allowed. |
| This network | `0.0.0.0/8`, `::` | On Linux, this reaches the app itself. |
| Multicast, reserved, broadcast | `224.0.0.0` and above, `ff00::/8` | No model provider is on these. |
| An IPv6 form of a refused IPv4 address | IPv4-mapped, IPv4-translated (`::ffff:0:0/96`), IPv4-compatible, NAT64 (`64:ff9b::/96`), 6to4 (`2002::/16`) | A stack or a router turns these into the IPv4 address. |
| Local-use NAT64 | `64:ff9b:1::/48` | Each site picks where the IPv4 address sits in this prefix, so no check can read it. |

Two checks apply each rule:

1. A save refuses an address that a person types as a literal.
2. `ConnectCallback` of the HTTP handler resolves the name, checks each address, and connects the socket to an address that passed. So a name cannot move to a refused address after a check. A connection that the pool keeps was checked when it opened.

The HTTP client follows no redirect and uses no proxy, so a provider cannot send a call to another address.

## What a person sees

The test button shows the words of a provider only when the body has the error format of that kind:

- `Anthropic`: `{"type":"error","error":{"type":"...","message":"..."}}`.
- `OpenAiCompatible`: `{"error":{"message":"...", ...}}` with a `type` or a `code` in `error`. This kind also takes the Anthropic form `{"type":"error",...}`, because some servers of this kind send it.

The app sends no `anthropic-workspace-id` header. A key of a workspace works, because the key holds its workspace. An Admin cannot choose another workspace for a key.

Any other body gets the status and a fixed text. So the page of an internal service does not reach the person. Each message loses each hidden character, and each form of the key: as it is, in any case, base64, base64url, hex, URL-encoded, HTML-encoded, and each cut of 8 characters or more. A failed run stores only a closed code.

## A result and the words of the person

A result follows the save rule of a Draft ([SES-5](../specs/sessions.md)). A run takes the number of the Cleaned Draft at its start as its base (`Session.CleanupBaseCleanedRevision`). A device result sends the number of the Cleaned Draft that it read, as `baseCleanedRevision`. If Cleaned is still at that number, the result replaces Cleaned, and the words before it stay as a Revision. So an edit made before the run starts is replaced on purpose, and the page says so before the run. If Cleaned moved after the base, by a hand edit, a rename, or a tag, Cleaned stays. The result is a Revision that waits, `waitingCleanupRevision` of the Session names it, and the page says that the result waits in History. `POST /sessions/{id}/cleanup/waiting/apply` puts it in Cleaned from the number of the current Draft only. An older number, a result that no longer waits, or a use while a new Cleanup runs gets 409. The Synopsis, the suggestions, and the proposals of the result apply in both cases.

## What an Admin can still learn

An Admin can type any address that the rules allow, so the test button is still a small probe of the network of the server:

- If a port is open, closed, or silent, from the fixed text and the time that the answer takes.
- The HTTP status of a service on that port.
- The `error.message` of a service whose body has the error format above.
- The model IDs of a service whose body has the shape of a model list. An ID with a hidden character or bad bytes is not offered. An ID or a display name that holds a form of the key is not offered. A key of fewer than 8 characters, such as a placeholder for a local server, hides no ID, because it is no secret and its letters are in most IDs.
- The services of the server of the app itself. Loopback is refused in a container, but the private address of the container or of its host on the network reaches the app and each port that the host opens. So an Admin can probe the app through its own hostname. This is the same reach that an Admin has from a shell on the network.

An Admin cannot learn the body of any other answer, and cannot make the app follow a redirect.

## The mismatch with the fakes

`tests/Recall.FakeModelProvider` checks what the two real APIs check, but it is not the real API. Its model list ignores `after_id` and the page size, and its model objects hold fewer fields than the real ones. A change to the adapters must also read the current API reference.
