# Capture makes the audio durable before any transcription starts

The product promise is that a person dictates and the entry is captured with no friction. So no failure can lose words. Each client writes the audio to durable storage before any transcription engine reads it. The browser stores each slice and deletes it only after the server confirms it. The iOS app writes a file, and after a kill it recovers the file and transcribes it on the next launch. The server takes slices with an idempotent, append-only upload, and a job appends the transcript to Raw as a new Revision. The server keeps the audio for 7 days after success, and for as long as needed after a failure.

The default transcription engine for the web app is the server, in a subprocess with one worker. The plan's R11, R13, and R15 build this.

Three of these values are agent defaults, and the operator can change them: 7-day audio retention, the server as the default web engine, and one worker.

## Evidence

The results are in [`docs/evidence/transcribe/`](../evidence/transcribe/), from a loaded M1 Max with synthetic speech.

- [`matrix.jsonl`](../evidence/transcribe/matrix.jsonl): the 2-CPU server run (`B-2c-5m-*`) measured a median RTF of 0.093 and a peak of 1.2 GB (`jobStats.cgPeak`). These are in-process numbers, and the design uses a subprocess. The 4-thread browser WASM run (`W-wasm-4t-5m-*`) measured a median RTF of 0.155 and a download of 661 MB. Its 3.3 GiB is `rssMaxProcKb`, the largest resident size of one process. Its macOS footprint was about 2.2 GB (`footprintMaxMb`).
- [`phase2.jsonl`](../evidence/transcribe/phase2.jsonl): the one subprocess run with two threads (`C-sub-t2-nice19-r1`) measured RTF 0.113 at nice 19. The one subprocess run with one thread (`C-sub-t1-nice19-r1`) measured RTF 0.203 at nice 19.
- [`phase2.jsonl`](../evidence/transcribe/phase2.jsonl): with a 1 GB limit, the in-process run (`B-floor-1g-r1`) ended with `fetch failed`, because the out-of-memory kill took the host. The subprocess run (`B-floor-1g-sub-r1`) ended with `child exit 137`, and the host stayed up.

Questions that stay open: Parakeet and the browser recorder in real iOS Safari, Parakeet memory on a real iPhone, interruption behavior, open-to-record time, and speed on a real NAS.

## Considered options

- **Text-first capture, in which a transcription engine makes the text and the audio goes away.** We did not choose this option, because it breaks the rule of the plan: a failed transcription engine then loses the words.
- **Browser transcription as the default.** We did not choose this option. It reaches 3.3 GiB resident in one process (a footprint of about 2.2 GB) on a desktop and needs a 661 MB download. It needs COOP and COEP headers for threads, and without threads it is slower. Reports put the iOS Safari tab limit far under that, though no run tested it. It can return later as a desktop option.
- **In-process transcription on the server.** We did not choose this option. An out-of-memory failure kills the host, and the journal with it.

## Consequences

- Audio is sensitive data on disk with no encryption at rest. `docs/install.md` must say so.
- The Parakeet model has its own license. R11 confirms the attribution terms before the first release.
- A slow NAS can make transcription take longer than the speech. The guess is RTF 0.2 to 0.4, and the real NAS run in R11 decides the published minimum.
