# Capture makes the audio durable before any transcription starts

The product promise is that a person dictates and the entry is captured with no friction. So no failure can lose words. Each client writes the audio to durable storage before any transcription engine reads it. The browser stores each slice and deletes it only after the server confirms it. The iOS app writes a file, and after a kill it recovers the file and transcribes it on the next launch. The server takes slices with an idempotent, append-only upload, and a job appends the transcript to Raw as a new Revision. The server keeps the audio for 7 days after success, and for as long as needed after a failure.

The default transcription engine for the web app is the server, in a subprocess with one worker. The plan's R11, R13, and R15 build this.

## Evidence

The results are in `~/.claude/orchestrate/recall/prototypes/transcribe/results/`, from a loaded M1 Max with synthetic speech.

- `matrix.jsonl`: the 2-CPU server run in process (`B-2c-5m-*`) measured a median RTF of 0.093. Appendix A of `~/.claude/orchestrate/recall/docs/plan.md` records 1.2 GB for it. The 4-thread browser WASM run (`W-wasm-4t-5m-*`) measured a median RTF of 0.155, a peak resident size of up to 3.3 GiB in one process, and a download of 661 MB.
- `phase2.jsonl`: the subprocess run with two threads (`C-sub-t2-nice19-r1`) measured RTF 0.113.
- `phase2.jsonl`: with a 1 GB limit, the in-process run (`B-floor-1g-r1`) ended with `fetch failed`, because the out-of-memory kill took the host. The subprocess run (`B-floor-1g-sub-r1`) ended with `child exit 137`, and the host stayed up.

Questions that stay open: Parakeet and the browser recorder in real iOS Safari, Parakeet memory on a real iPhone, interruption behavior, open-to-record time, and speed on a real NAS.

## Considered options

- **Text-first capture, in which a transcription engine makes the text and the audio goes away.** We did not choose this option, because it breaks the rule of the plan: a failed transcription engine then loses the words.
- **Browser transcription as the default.** We did not choose this option. It reaches 3.3 GiB resident on a desktop and needs a 661 MB download. It needs COOP and COEP headers for threads, and without threads it is slower. Reports put the iOS Safari tab limit far under 3.3 GiB, though no run tested it. It can return later as a desktop option.
- **In-process transcription on the server.** We did not choose this option. An out-of-memory failure kills the host, and the journal with it.

## Consequences

- Audio is sensitive data on disk with no encryption at rest. `docs/install.md` must say so.
- The Parakeet model has its own license. R11 confirms the attribution terms before the first release.
- A slow NAS can make transcription take longer than the speech. The guess is RTF 0.2 to 0.4, and the real NAS run in R11 decides the published minimum.
