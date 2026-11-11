# Mic

The journal home opens with a mic card above `New entry`. One tap on `Record` records into a new entry. Each second of audio saves in the browser before it goes to the Server, so the audio outlives a closed tab, a reload, a lost network, and a stopped Server. The card follows the audio until the words of the recording are in the journal.

## Sub-features

- `record` starts a recording with one tap on `Record`. The card shows `Recording`, the time, and the level. `Stop recording` ends it.
- `states` shows `Saved on this device`, `Sending <time> of audio` with an `Upload` progress bar, `Writing the words`, and `Your words are in` with the words and `Open entry`.
- `closed-tab` keeps the audio of a tab that closed or reloaded while it recorded. The next page of the same person ends that recording and sends it.
- `offline` shows `No network. The upload waits` and sends the audio when the network comes back.
- `server-down` shows `The server does not answer. The upload waits` and sends the audio when the Server answers again.
- `failure` shows `No words came from this audio` with `Try again` and `Open entry`. A refusal of an upload shows its words with `Send again`.
- `blocked` shows `The browser blocks the mic` with `Try again` and `Type instead`, `The mic needs HTTPS` on plain HTTP with `Turn on HTTPS`, and `This browser does not keep audio` when the browser refuses its store.
- `engine` shows `Transcribe on: <place>` with `Change`, which opens `/transcription-engine`.

## How to get to it (user POV)

- Open `/`, or choose `Journal` in the sidebar. The card `Record an entry` is the first card under the date.
- Choose `Change` next to `Transcribe on` to pick the Server or a Cloud model provider.

## Driving it with browse.mjs

Preconditions:

- `doctor.sh` exits with 0.
- The recipe exports `needsMic`, so `browse.mjs` starts the full Chromium build with a fake mic and allows the mic for the web URL. With no flag, the fake mic beeps. A beep has no words, so the recipe then proves the failure state instead of the words.
- To prove the words, add `--fake-mic <speech.wav>`. The file is a WAV of speech, for example from `say -o speech.wav --data-format=LEI16@16000 "<words>"` on macOS.
- The engine of the stack can make words. A Server engine downloads its speech model at the first transcription, so the first run waits up to 15 minutes. A Cloud engine needs a connected model provider (see [Model providers](./model-providers.md)).

- **Record.** Choose `Record` in the region `Record an entry`. The status of the region says `Recording`. The newest row of the IndexedDB store `captures` in the database `recall.capture` counts `storedSlices` up by about one each second.
- **Closed tab.** Run `page.reload()` while the card records. The status says `Sending`, `Writing the words`, or `Your words are in` within 60 seconds. Do not count the rows of the store `slices`, because a slice leaves the store when the Server confirms it.
- **Failure.** With the beep, the status says `No words came from this audio` and `The audio is safe on the server`. `Try again` is enabled.
- **Text.** With speech, the status says `Your words are in`. `Open entry` goes to `/sessions/<id>`, `GET /api/v1/sessions/<id>` has a `rawText` that is not empty, and `Your words` shows the words.
- **Offline.** Run `context.setOffline(true)` while the card records, then choose `Stop recording`. The status says `No network. The upload waits`. Run `context.setOffline(false)`. The status says `Writing the words` or `Your words are in`.
- **Proof.** Run `node $S/scripts/browse.mjs $S/recipes/mic.mjs --fake-mic <speech.wav>`. The captures `mic-idle`, `mic-recording`, `mic-after-reload`, `mic-result`, `mic-entry`, `mic-offline`, and `mic-back-online` show each state. With no flag, `mic-entry` is not there, and `mic-result` shows the failure.

## Gotchas

- The headless shell of Playwright answers `NotSupportedError` to `getUserMedia`. For this reason, a recipe with `needsMic` runs in the full Chromium build.
- The mock mic of WebKit in Playwright plays a steady tone, not speech, so a Server engine finds no words in it. To prove Safari, play speech through a stub of `MediaDevices.prototype.getUserMedia`.
- A page on `localhost` is a secure context, so the mic works on plain HTTP there. To see `The mic needs HTTPS`, open the stack on a host name other than `localhost` over HTTP.
- A write to the API without the header `X-Recall-User` gets 409 `page-user-mismatch`. The SPA sends it. A recipe that writes with `fetch` must send it too.
