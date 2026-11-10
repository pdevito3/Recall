import type { RecordingDto } from '@/api/generated/models/types/recording-dto'

import type { StoredCapture } from './audio-store'

export type MicFailure = 'denied' | 'noMic' | 'micBusy' | 'micFailed' | 'unsupported'

/** The mic of this tab. What happens to the audio after the recorder stops is in the store, not here. */
export type MicState =
  | { kind: 'idle' }
  | { kind: 'starting' }
  | { kind: 'recording'; startedAt: number }
  | { kind: 'failed'; failure: MicFailure }
  /** This browser refused to store a slice, so the recording stopped with the slices that are safe. */
  | { kind: 'storeFailed' }

export type MicEvent =
  | { type: 'tap' }
  | { type: 'started'; startedAt: number }
  | { type: 'startFailed'; failure: MicFailure }
  | { type: 'ended' }
  | { type: 'storeFailed' }
  /** The browser says at page open that this site may not use the mic. */
  | { type: 'permissionDenied' }
  /** The person allowed the mic in the settings of the site, so the denied card goes. */
  | { type: 'permissionAllowed' }

export function micReducer(state: MicState, event: MicEvent): MicState {
  switch (event.type) {
    case 'tap':
      return state.kind === 'starting' || state.kind === 'recording' ? state : { kind: 'starting' }
    case 'started':
      return state.kind === 'starting' ? { kind: 'recording', startedAt: event.startedAt } : state
    case 'startFailed':
      return state.kind === 'starting' ? { kind: 'failed', failure: event.failure } : state
    case 'ended':
      return state.kind === 'recording' ? { kind: 'idle' } : state
    case 'storeFailed':
      return { kind: 'storeFailed' }
    case 'permissionDenied':
      return state.kind === 'idle' ? { kind: 'failed', failure: 'denied' } : state
    case 'permissionAllowed':
      return state.kind === 'failed' && state.failure === 'denied' ? { kind: 'idle' } : state
  }
}

export type CaptureView =
  | { kind: 'insecure' }
  | { kind: 'idle' }
  | { kind: 'starting' }
  | { kind: 'recording'; startedAt: number }
  | { kind: MicFailure }
  | { kind: 'storeFailed' }
  /** The browser refuses to read or keep the audio store, as some private windows do, so a recording could not be safe. */
  | { kind: 'noStore' }
  /** The recording ended, and its audio waits in this browser for the upload to start. */
  | { kind: 'stored'; durationMs: number }
  | { kind: 'sending'; sentSlices: number; totalSlices: number; durationMs: number }
  | { kind: 'offline'; durationMs: number }
  | { kind: 'serverDown'; durationMs: number }
  | { kind: 'refused'; text: string; clientRecordingId: string }
  | { kind: 'transcribing'; problemText: string | null }
  | { kind: 'ready'; sessionId: string }
  | { kind: 'failed'; recordingId: string; sessionId: string; problemText: string | null }

export type CaptureKind = CaptureView['kind']

export interface CaptureInputs {
  isSecure: boolean
  canRecord: boolean
  /** False when the audio store of the browser refused the read. */
  canStore: boolean
  mic: MicState
  /** The newest capture of the person in this browser. */
  latest: StoredCapture | null
  /** The Server row of `latest`, once the Server has all of its audio. */
  recording: RecordingDto | undefined
  isOnline: boolean
  now: number
}

/** A text that is ready shows until this long after the recording, so a page open the next day starts at the mic. */
export const readyShowsForMs = 60 * 60 * 1000

function viewOfUpload(capture: StoredCapture, isOnline: boolean): CaptureView {
  const { durationMs } = capture
  if (capture.problem?.kind === 'refused') return { kind: 'refused', text: capture.problem.text, clientRecordingId: capture.clientRecordingId }
  if (!isOnline || capture.problem?.kind === 'offline') return { kind: 'offline', durationMs }
  if (capture.problem?.kind === 'serverDown') return { kind: 'serverDown', durationMs }
  if (capture.isEnded && capture.sentSlices === 0 && capture.recordingId === null) return { kind: 'stored', durationMs }
  return { kind: 'sending', sentSlices: capture.sentSlices, totalSlices: capture.storedSlices, durationMs }
}

function viewOfText(capture: StoredCapture, recording: RecordingDto | undefined, now: number): CaptureView {
  if (!recording || recording.status === 'Receiving' || recording.status === 'Received' || recording.status === 'Transcribing') {
    return { kind: 'transcribing', problemText: recording?.problemText ?? null }
  }
  if (recording.status === 'Failed') return { kind: 'failed', recordingId: recording.id, sessionId: recording.sessionId, problemText: recording.problemText ?? null }
  const endedAt = Date.parse(capture.startedAt) + capture.durationMs
  return now - endedAt < readyShowsForMs ? { kind: 'ready', sessionId: recording.sessionId } : { kind: 'idle' }
}

/** What the mic card shows. A recording of this tab comes first, then the newest capture, then the mic at rest. */
export function captureViewOf({ isSecure, canRecord, canStore, mic, latest, recording, isOnline, now }: CaptureInputs): CaptureView {
  if (!isSecure) return { kind: 'insecure' }
  if (!canRecord) return { kind: 'unsupported' }
  if (!canStore) return { kind: 'noStore' }
  if (mic.kind === 'starting' || mic.kind === 'recording' || mic.kind === 'storeFailed') return mic
  if (mic.kind === 'failed') return { kind: mic.failure }
  if (!latest) return { kind: 'idle' }
  if (!latest.isFinished) return viewOfUpload(latest, isOnline)
  return viewOfText(latest, recording, now)
}

/** The captures other than the newest that still hold audio in this browser. */
export const waitingBesides = (captures: StoredCapture[]) => captures.slice(0, -1).filter((capture) => !capture.isFinished).length

/** A tap starts a recording in each state but these. */
export const canTapToRecord = (kind: CaptureKind) => kind !== 'insecure' && kind !== 'unsupported' && kind !== 'noStore' && kind !== 'starting'
