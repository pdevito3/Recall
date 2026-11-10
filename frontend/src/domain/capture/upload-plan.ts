import type { SessionDto } from '@/api/generated/models/types/session-dto'

import type { StoredCapture, UploadProblem } from './audio-store'
import { readyShowsForMs } from './capture-view'

/** The next call that moves a capture to the Server. Each call is idempotent on the Server, so a repeat after a lost answer is safe. */
export type UploadStep =
  | { kind: 'createEntry' }
  | { kind: 'createRecording' }
  | { kind: 'sendSlice'; index: number }
  | { kind: 'finish'; sliceCount: number; byteLength: number }
  /** The recorder still runs, and each stored slice is sent. */
  | { kind: 'waitForSlices' }
  /** The recorder stopped before its first slice, so there is no audio to keep. */
  | { kind: 'discard' }
  | { kind: 'done' }

export function nextUploadStep(capture: StoredCapture): UploadStep {
  if (capture.isFinished) return { kind: 'done' }
  // An entry with no audio would show as an empty entry on the timeline, so the entry waits for the first slice.
  if (capture.storedSlices === 0) return capture.isEnded ? { kind: 'discard' } : { kind: 'waitForSlices' }
  if (capture.sessionId === null) return { kind: 'createEntry' }
  if (capture.recordingId === null) return { kind: 'createRecording' }
  if (capture.sentSlices < capture.storedSlices) return { kind: 'sendSlice', index: capture.sentSlices }
  if (!capture.isEnded) return { kind: 'waitForSlices' }
  return { kind: 'finish', sliceCount: capture.storedSlices, byteLength: capture.storedBytes }
}

/** What the queue does after a step fails. */
export type FailureVerdict =
  | { kind: 'retry'; problem: UploadProblem }
  /** The person deleted the new entry before its audio came. The audio goes to a new entry, so no word is lost. */
  | { kind: 'newEntry' }
  /** The person deleted the entry, and the Server deleted its Recording with it. The rest of the audio goes too. */
  | { kind: 'gone' }
  | { kind: 'refused'; problem: UploadProblem }

export interface StepFailure {
  /** The HTTP status, or null when no answer came. */
  status: number | null
  text: string
  isOnline: boolean
}

const waitsAndTriesAgain = (status: number) => status === 401 || status === 408 || status === 429 || status >= 500

export function verdictOf(step: UploadStep, { status, text, isOnline }: StepFailure): FailureVerdict {
  if (status === null || waitsAndTriesAgain(status)) return { kind: 'retry', problem: isOnline ? { kind: 'serverDown' } : { kind: 'offline' } }
  // The create of a deleted Session gives 409, and a Recording in a deleted Session gives 404.
  if ((step.kind === 'createEntry' && status === 409) || (step.kind === 'createRecording' && status === 404)) return { kind: 'newEntry' }
  if ((step.kind === 'sendSlice' || step.kind === 'finish') && status === 404) return { kind: 'gone' }
  return { kind: 'refused', problem: { kind: 'refused', text } }
}

const firstWaitMs = 1_000
const longestWaitMs = 30_000

/** The wait before try `failures + 1`: 1 s, doubled up to 30 s, with up to half of it again at random, so that many tabs do not try at one moment. */
export function retryDelayMs(failures: number, random: number): number {
  const base = Math.min(longestWaitMs, firstWaitMs * 2 ** Math.max(0, failures - 1))
  return Math.round(base * (1 + random / 2))
}

/** The capture that the queue works on next: the oldest with a step to take. */
export function nextCaptureToSend(captures: StoredCapture[]): StoredCapture | null {
  return captures.find((capture) => {
    const step = nextUploadStep(capture).kind
    return step !== 'done' && step !== 'waitForSlices' && capture.problem?.kind !== 'refused'
  }) ?? null
}

/**
 * Finished captures hold no audio, only what a card needs to follow the words. A tab follows its own capture, also when
 * another tab recorded after it, so each one stays while its words can show, and the newest stays for good.
 */
export function finishedCapturesToForget(captures: StoredCapture[], now: number): StoredCapture[] {
  const newest = captures[captures.length - 1]
  const endedAt = (capture: StoredCapture) => Date.parse(capture.startedAt) + capture.durationMs
  return captures.filter((capture) => capture.isFinished && capture !== newest && now - endedAt(capture) >= readyShowsForMs)
}

/** The captures that hold audio that the Server does not have all of. */
export const unsentAudioOf = (captures: StoredCapture[]) => captures.filter((capture) => !capture.isFinished && capture.storedSlices > 0)

/** The send before a sign-out is done when no capture can move: each one left waits for the person to send it again. */
export const isNothingLeftToTry = (unsent: StoredCapture[]) => unsent.every((capture) => capture.problem?.kind === 'refused')

/** An entry that never held text, a tag, or another Recording, so a discard of the recording that made it can take it too. */
export const isUntouchedEntry = (entry: SessionDto, recordingCount: number) =>
  recordingCount === 0 &&
  entry.rawDraftRevision === 0 &&
  entry.rawLatestRevision === 0 &&
  entry.cleanedDraftRevision === 0 &&
  entry.cleanedLatestRevision === 0 &&
  entry.metadataVersion === 0

/** The length of the audio that the Server does not have. Each slice is about a second, so the share of the slices tells it. */
export const unsentMsOf = ({ durationMs, storedSlices, sentSlices }: StoredCapture) =>
  storedSlices === 0 ? durationMs : Math.round((durationMs * (storedSlices - sentSlices)) / storedSlices)

/** Changes each time a step of an upload works, so that the send before a sign-out can tell that it still moves. */
export const progressOf = (unsent: StoredCapture[]) =>
  unsent.map((capture) => `${capture.clientRecordingId}:${capture.sessionId}:${capture.recordingId}:${capture.sentSlices}`).join(',')
