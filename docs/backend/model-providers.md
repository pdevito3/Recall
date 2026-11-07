# Model providers

A Cleanup sends the words of a Session to a model provider. The address of the provider comes from an Admin, and the answer comes from a service that the app does not control. This page gives the rules that hold for each call. Each rule has a test in `ProviderLimitTests`, `ProviderTextAndAddressTests`, or `ModelProviderClientTests`.

## The active provider

A connect does not change which provider a Cleanup uses. A provider is active only when a person chooses it and it has a model.

- A new provider has no `ActivatedOn`. Connect it, test it, pick a model, and then choose **Make active**.
- A choice of a provider with no model gets 422.
- A provider that loses its model stops being active, and a person must choose it again.
- The newest choice of a tenant is the active provider. If that provider is deleted, the choice before it is active again.
- With no active provider, a Cleanup start gets 422, the page says why, and its button stays off.

## Limits of a call

`ProviderLimits` holds each value. Each read on each adapter goes through `ProviderReadGuard`, which is a handler of the HTTP client. So the limits also hold for the stream of the Anthropic SDK, the model list, and each error body.

| Limit | Value | Why |
|---|---|---|
| Connect | 10 s | A host that answers on a network answers in this time. A person waits for the test button. |
| No progress on a read, also for the headers | 3 min | A local model can load and read a long prompt before its first byte. Anthropic sends `ping` events during a stream. A hung call ends here. |
| A whole call for words, and all tries of one run | 40 min | The longest answer is 64 000 tokens. At 30 tokens each second, this is about 36 min. A wait in the queue does not count. |
| A call for the model list | 30 s | A person waits for it, and no model runs. |
| A body that is not a stream | 16 MiB | A list of models or an error is far smaller. A `Content-Length` above the limit ends the call before a read. |
| A stream | 64 MiB | 250 000 tokens with an envelope of up to 250 bytes for each. |
| One line of a stream | 4 MiB | The text of one event goes to memory as a whole. |
| The words of an answer | 2 x the limit of a Draft | The longest answer that can give a Draft, with an escape for each character. |
| Pages of the Anthropic model list | 10 pages of 1000, and 2000 models | `has_more` comes from the provider, so a list that never ends must stop. |

A hung provider ends a run in about 19 minutes: 6 tries of 3 minutes, and 62 s of waits between them. A stream that makes progress can run for the full 40 minutes.

## What a failure means

| Fault | Kind | What the run does |
|---|---|---|
| No answer, a cut connection, a read with no progress, a status of 408, 409, 429, or 5xx | `Unavailable` | Tries again, up to 6 calls. Then `ProviderUnavailable`. |
| Another status | `Refused` | Ends at once as `ProviderRefused`. |
| Invalid JSON, an HTML body with status 200, a bad charset, a body or a stream over its limit, an empty stream, a cut answer (`max_tokens` or `length`), a refusal | `BadOutput` | Ends at once as `BadModelOutput`. The same bytes come again on a retry. |
| A refused address | `Refused` | Ends at once as `ProviderRefused`. |

The test button always answers with 200 and with one of these kinds. A person can also stop a run: see [The Cleanup job](background-jobs.md#the-cleanup-job).

## Addresses

A private address stays allowed, because a local model on a home network is a real use. `ProviderAddressRule` refuses:

| Rule | Addresses | Why |
|---|---|---|
| Link-local | `169.254.0.0/16`, `fe80::/10` | This range holds the metadata address of most clouds. |
| Named metadata addresses | `fd00:ec2::254`, `100.100.100.200`, `168.63.129.16`, `192.0.0.192` | These cloud services are outside the link-local range. |
| Loopback, in a container | `127.0.0.0/8`, `::1`, `localhost` | In a container, a loopback address is the app itself, and a local model is on another address. Outside a container, for example in development, loopback stays allowed. |
| This network | `0.0.0.0/8`, `::` | On Linux, this reaches the app itself. |
| Multicast, reserved, broadcast | `224.0.0.0` and above, `ff00::/8` | No model provider is on these. |
| An IPv6 form of a refused IPv4 address | IPv4-mapped, IPv4-compatible, NAT64 (`64:ff9b::/96`), 6to4 (`2002::/16`) | A stack or a router turns these into the IPv4 address. |

Two checks apply each rule:

1. A save refuses an address that a person types as a literal.
2. `ConnectCallback` of the HTTP handler resolves the name, checks each address, and connects the socket to an address that passed. So a name cannot move to a refused address after a check. A connection that the pool keeps was checked when it opened.

The HTTP client follows no redirect and uses no proxy, so a provider cannot send a call to another address.

## What a person sees

The test button shows the words of a provider only when the body has the error format of that kind:

- `Anthropic`: `{"type":"error","error":{"type":"...","message":"..."}}`.
- `OpenAiCompatible`: `{"error":{"message":"...", ...}}` with a `type` or a `code` in `error`.

Any other body gets the status and a fixed text. So the page of an internal service does not reach the person. Each message loses each hidden character, and each form of the key: as it is, in any case, base64, base64url, URL-encoded, HTML-encoded, and each cut of 8 characters or more. A failed run stores only a closed code.

## What an Admin can still learn

An Admin can type any address that the rules allow, so the test button is still a small probe of the network of the server:

- If a port is open, closed, or silent, from the fixed text and the time that the answer takes.
- The HTTP status of a service on that port.
- The `error.message` of a service whose body has the error format above.
- The model IDs of a service whose body has the shape of a model list. An ID with a hidden character or bad bytes is not offered.

An Admin cannot learn the body of any other answer, and cannot make the app follow a redirect.

## The mismatch with the fakes

`tests/Recall.FakeModelProvider` checks what the two real APIs check, but it is not the real API. Its model list ignores `after_id` and the page size, and its model objects hold fewer fields than the real ones. A change to the adapters must also read the current API reference.
