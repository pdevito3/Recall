# Spec: audio

## 1. Scope

A person sends audio to the server, in slices, and the server turns it into text in the Raw of a Session. The server stores the audio until the text is saved. A word must never vanish: audio that did not become text stays stored, and the person can see why.

This spec covers the `Recording`, its slices, the jobs that make its text, the speech model, the cloud engine, and the audio files. It leaves out each screen (R12) and the connection of a provider (R9 owns it, see [model-providers.md](../backend/model-providers.md)).

Plan section R11 gives the reasons.

## 2. Entities and states

The `recordings` table is not in the sync feed. The text of a Recording reaches each device through the feed of its Session, and the upload state is of use only to the device that uploads. The table has its own check that refuses a row of a deleted owner.

| Entity | Root of | Fields that a user sets | States |
|---|---|---|---|
| `Recording` | `RecordingSlice` | client recording ID, format | Receiving, Received, Transcribing, Transcribed, Failed |

| From | To | Method | Who |
|---|---|---|---|
| Receiving | Received | `Recording.Finish` | The owner |
| Received, Transcribing | Transcribing | `Recording.BeginAttempt` | The transcription job |
| Received | Received, with the problem `WaitingForModel` | `Recording.WaitForModel` | The transcription job |
| Transcribing | Received, with the problem `ProviderUnavailable` | `Recording.WaitForProvider` | The cloud transcription job |
| Received, Transcribing | Received, on the local engine | `Recording.HandToLocalEngine` | The cloud transcription job |
| Transcribing | Transcribed | `Recording.Complete` | The transcription job |
| Received, Transcribing | Failed | `Recording.Fail` | The transcription job |
| Failed | Received | `Recording.Retry` | The owner |

`Recording.Complete` gives Failed with `NoWordsFound` when no chunk had words. `Recording.BeginAttempt` gives Failed with `TooManyAttempts` after `MaxAttemptsWithoutProgress` attempts with no saved chunk, or with `ProviderUnavailable` when the provider did not answer the last of them.

## 3. Rules

| ID | Rule | Enforced by | Backstop |
|---|---|---|---|
| REC-1 | One client recording ID of a User makes one Recording. A replay of the create returns that Recording with 200. | `AddRecording` | Unique index on `recordings.user_id, client_recording_id` |
| REC-2 | A slice is idempotent and append-only. A repeat with the same bytes gives the same answer. Other bytes for a stored index give 409. Slices can come in any order. | `StoreRecordingSlice` | Primary key of `recording_slices` |
| REC-3 | A finish needs each slice from 0 to the count, and the same total of bytes. Otherwise it gives 409, and the Recording stays Receiving. A repeat of the finish gives the same answer. | `Recording.Finish` | |
| REC-4 | The server checks each upload. The first slice must start as the format of the Recording, a body must be `application/octet-stream`, and a body over 8 MiB gives 413. | `StoreRecordingSlice`, `RecordingFiles.StartsAsFormat` | Request size limit of the route |
| REC-5 | The audio stays until the text is saved. After success it stays 7 days. After a failure it stays until the person deletes the Recording. | `Recording.CanDeleteAudio` | |
| REC-6 | Each chunk of text joins Raw as a new Revision, with the sample pointer in the same save. A job that runs again goes on from the pointer, so no text repeats. A chunk moves the number of the Draft, so by [SES-5](sessions.md) a Raw save from before the chunk gets `Lost` at any save time, and the chunk stays in the Draft. The save stays as a Revision, and the client reads `Lost`. | `SaveTranscriptChunk`, `Recording.AdvanceTo`, `DraftSlot.Decide` | |
| REC-7 | A job that fails, a restart, and a lease return leave no Recording stuck. Attempts with no saved chunk end at `MaxAttemptsWithoutProgress`, before the ceiling of 10 job attempts, as Failed with a reason. The sweep queues each Recording that waits, at each start and each hour. A repeat finish and a retry also queue one. | `Recording.BeginAttempt`, `RecordingSweep` | |
| REC-8 | A job payload holds IDs. No log, span, support bundle, or job row gets a word of a transcript, and the output of the worker is not logged. | `JobPayloadTests`, `LocalParakeetEngine` | |
| REC-9 | The local engine sends nothing out. The one network call is a GET for a file of the model, at a pinned commit, and a file that fails its pinned SHA-256 is rejected. | `DownloadModelJob`, `ModelManifest` | The worker has no network code |
| REC-10 | Only the owner reads a Recording or its file. With no session the answer is 401, and for each other caller it is 404. A file name other than the name of the audio gives 404. | `GetRecordingFile`, the journal owner filter | |
| REC-11 | A tenant, owner, or Recording delete leaves no file. A failed delete gets a retry job, a kill between the commit and the delete is cleared at the next start, and a late write removes its own file. | `DeleteTenant`, `RecordingSweep`, `StoreRecordingSlice` | The sweep at each start |
| REC-12 | A machine with no model and no network stores the audio with a true status. The Recording stays Received with the reason, and the download tries again. | `BeginTranscription`, `DownloadModelJob` | |
| REC-13 | At most one transcription runs at a time, in a subprocess that a token stop kills. The memory of the host is out of reach of the worker: the kernel kills the worker first. | The `transcription` worker group with a pool of 1, `LocalParakeetEngine` | |
| REC-14 | A person can see that the jobs that make text stopped, with what to do. A Recording that waits for its text says so in `problemText`, and its `status` and `problem` stay as they are. A stop of the other job group shows at `/health` only. | `GetTranscriptionStatus`, `RecordingViews.ToDto`, `JobHostStatus.TranscriptionIsStopped` | |
| REC-15 | A failure names a cause only when the server has seen it. `DiskFull` and `DiskRefused` need the words of the disk error from ffmpeg. `AudioUnreadable` needs the words that ffmpeg 7.1.3 uses for bad audio, and only this problem tells a person to record again. Other ffmpeg failures read `EngineCrashed`. `OutOfMemory` needs a kill of the engine and a rise of `oom_kill` in `memory.events` during the same try. The count is for the whole container, so the text says that both happened and does not say that one caused the other. Other kills read `EngineKilled`, with no claim of a cause, also when the kill cuts a line of the worker. | `LocalParakeetEngine` | |
| REC-16 | The decoded copy of the audio, whole or partial, lasts only while a Recording waits for its text. The job removes it when the Recording ends as Transcribed or Failed, and the sweep at each start removes one that a kill left. The audio stays by REC-5, and a retry decodes it again. | `RemoveEndedWorkAudio`, `RecordingSweep` | |
| REC-17 | An Admin chooses one model provider that takes audio, with its model, for the tenant. A Recording takes the engine of the tenant at its finish, and at a retry after a failure. A job of the other engine leaves it alone, so the text of two engines never mixes. Anthropic takes no audio, and a choice of it gives 422. | `ChooseTranscriptionEngine`, `Recording.Finish`, `Recording.Retry`, `BeginTranscription`, `SaveTranscriptChunk` | |
| REC-18 | The cloud engine sends the decoded 16 kHz mono WAV, so each format of a client reaches a provider in a format that it takes. A part holds at most 2 minutes, inside the output limit of 2,000 tokens of `gpt-4o-transcribe`, and at most 25,000,000 bytes, the limit of OpenAI and Groq read as decimal MB. A part over the limit ends at the quietest 50 ms in the last 5 s before it, and at least 1 s before the end of the audio, and the next part starts at that sample, so the text of each part joins once, in order, with no gap. A part is saved as a chunk by REC-6, so a lease return sends only the parts after the pointer. An answer that the provider cut at its output limit is not saved, and its part goes again as two. | `CloudTranscriptionEngine`, `AudioParts`, `PcmAudio`, `OpenAiCompatibleClient.TranscribeAsync` | Each part is under the limit by its construction. `ProviderLimits.AudioOutputTokens` |
| REC-19 | A provider that refuses fails the Recording with `ProviderRefused`, a bad answer with `ProviderBadAnswer`, and a key that the server cannot read with `ProviderKeyUnreadable`. A timeout, a hang past the idle limit, a 408, a 429, and a 5xx leave it Received with `ProviderUnavailable`, and the job tries again after 1, 2, 5, 10, and 15 minutes. After `MaxAttemptsWithoutProgress` such tries it fails with `ProviderUnavailable`, and its text tells the person to try again, since no try comes on its own. In each case the audio stays. | `CloudTranscriptionEngine`, `TranscribeRecordingJob`, `Recording.BeginAttempt`, `RecordingViews.ProviderGaveUpText` | `ProviderLimits.AudioIdle`, `ProviderLimits.AudioCall` |
| REC-20 | A cloud transcription runs in the queue of the provider calls of its tenant. A hung provider of one tenant holds only that queue, and the next job of another tenant goes to a queue with no work of the hung tenant. A queue runs jobs side by side, so the sweep gives a cloud Recording a job only when it has none that has not ended, and no part goes to the provider twice. | `CleanupQueues.Pick`, `CleanupQueues.UnfinishedAsync`, `CleanupQueues.RecordingsInCloudJobsAsync` | |
| REC-21 | With no chosen provider, the local engine makes the text and the audio stays on the server. The engine route says why: no provider takes audio, or none is chosen. A cloud job whose choice ended before it ran gives the Recording to the local engine and sends no audio. A job reads the choice again after each part, so after a change no new part goes to the old provider: the job begins again from the saved text with the new choice, or gives the Recording to the local engine. Only the part in flight at the change ends at the old provider. | `GetTranscriptionEngine`, `BeginTranscription` | |
| REC-22 | No job row, log, span, support bundle, or dashboard page gets a word of a transcript, a byte of audio, or the key of a provider. A failure that a job keeps is a fixed text. | `JobPayloadTests`, `JobSafeTextException`, `ProviderFailureText` | |

## 4. Rejected rows

None.
