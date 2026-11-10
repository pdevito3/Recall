import { describe, expect, it } from 'vitest'

import type { RecordingDto } from '@/api/generated/models/types/recording-dto'

import type { StoredCapture } from './audio-store'
import { captureViewOf, micReducer, readyShowsForMs, waitingBesides, type CaptureInputs, type MicState } from './capture-view'

const startedAt = Date.parse('2026-11-10T08:00:00Z')

const aCapture = (changes: Partial<StoredCapture> = {}): StoredCapture => ({
  clientRecordingId: 'c1',
  owner: 'ada',
  clientSessionId: 's1',
  startedAt: new Date(startedAt).toISOString(),
  format: 'Webm',
  storedSlices: 10,
  storedBytes: 10_000,
  sentSlices: 0,
  durationMs: 10_000,
  isEnded: true,
  sessionId: null,
  recordingId: null,
  isFinished: false,
  problem: null,
  touchedAt: startedAt,
  ...changes,
})

const aRecording = (changes: Partial<RecordingDto> = {}): RecordingDto => ({
  id: 'rec_1',
  sessionId: 'ses_1',
  clientRecordingId: 'c1',
  format: 'Webm',
  engine: 'Local',
  status: 'Received',
  problem: 'None',
  problemText: null,
  storedSlices: [],
  byteLength: 10_000n,
  transcribedMs: 0n,
  version: 1,
  ...changes,
})

const inputs = (changes: Partial<CaptureInputs> = {}): CaptureInputs => ({
  isSecure: true,
  canRecord: true,
  mic: { kind: 'idle' },
  latest: null,
  recording: undefined,
  isOnline: true,
  now: startedAt + 20_000,
  ...changes,
})

describe('the mic of a tab', () => {
  it('goes from a tap to recording, and back to rest when the recorder stops', () => {
    const started = micReducer(micReducer({ kind: 'idle' }, { type: 'tap' }), { type: 'started', startedAt })
    expect(started).toEqual({ kind: 'recording', startedAt })
    expect(micReducer(started, { type: 'ended' })).toEqual({ kind: 'idle' })
  })

  it('ignores a second tap while the mic starts or records, so one tap makes one recording', () => {
    const starting: MicState = { kind: 'starting' }
    const recording: MicState = { kind: 'recording', startedAt }
    expect(micReducer(starting, { type: 'tap' })).toBe(starting)
    expect(micReducer(recording, { type: 'tap' })).toBe(recording)
  })

  it('keeps the reason of a failed start until the next tap', () => {
    const denied = micReducer({ kind: 'starting' }, { type: 'startFailed', failure: 'denied' })
    expect(denied).toEqual({ kind: 'failed', failure: 'denied' })
    expect(micReducer(denied, { type: 'tap' })).toEqual({ kind: 'starting' })
  })

  it('shows a denial that the browser reports at page open, and drops it when the person allows the mic', () => {
    const denied = micReducer({ kind: 'idle' }, { type: 'permissionDenied' })
    expect(denied).toEqual({ kind: 'failed', failure: 'denied' })
    expect(micReducer(denied, { type: 'permissionAllowed' })).toEqual({ kind: 'idle' })
    expect(micReducer({ kind: 'failed', failure: 'noMic' }, { type: 'permissionAllowed' })).toEqual({ kind: 'failed', failure: 'noMic' })
  })

  it('does not let a late permission answer stop a recording', () => {
    const recording: MicState = { kind: 'recording', startedAt }
    expect(micReducer(recording, { type: 'permissionDenied' })).toBe(recording)
  })

  it('stops with a storage failure from any state', () => {
    expect(micReducer({ kind: 'recording', startedAt }, { type: 'storeFailed' })).toEqual({ kind: 'storeFailed' })
  })
})

describe('the mic card', () => {
  it('says that the mic needs HTTPS on plain HTTP, before any other state', () => {
    expect(captureViewOf(inputs({ isSecure: false, mic: { kind: 'recording', startedAt } })).kind).toBe('insecure')
  })

  it('says that the browser cannot record when it has no recorder', () => {
    expect(captureViewOf(inputs({ canRecord: false })).kind).toBe('unsupported')
  })

  it('rests at the mic when this browser has no capture', () => {
    expect(captureViewOf(inputs()).kind).toBe('idle')
  })

  it('shows the recording of this tab over the upload of an earlier capture', () => {
    expect(captureViewOf(inputs({ mic: { kind: 'recording', startedAt }, latest: aCapture({ sentSlices: 3, recordingId: 'rec_1', sessionId: 'ses_1' }) }))).toEqual({ kind: 'recording', startedAt })
  })

  it('shows each reason that the mic did not start', () => {
    for (const failure of ['denied', 'noMic', 'micBusy', 'micFailed'] as const) {
      expect(captureViewOf(inputs({ mic: { kind: 'failed', failure } })).kind).toBe(failure)
    }
  })

  it('says that the audio is on this device when the recording ended and the upload has not started', () => {
    expect(captureViewOf(inputs({ latest: aCapture() }))).toEqual({ kind: 'stored', durationMs: 10_000 })
  })

  it('counts the slices that the Server confirmed while it sends', () => {
    const latest = aCapture({ sessionId: 'ses_1', recordingId: 'rec_1', sentSlices: 4 })
    expect(captureViewOf(inputs({ latest }))).toEqual({ kind: 'sending', sentSlices: 4, totalSlices: 10, durationMs: 10_000 })
  })

  it('says that the upload waits for the network when the browser is offline', () => {
    const latest = aCapture({ sessionId: 'ses_1', recordingId: 'rec_1', sentSlices: 4 })
    expect(captureViewOf(inputs({ latest, isOnline: false })).kind).toBe('offline')
    expect(captureViewOf(inputs({ latest: { ...latest, problem: { kind: 'offline' } } })).kind).toBe('offline')
  })

  it('says that the upload waits for the server when the server does not answer', () => {
    const latest = aCapture({ sessionId: 'ses_1', recordingId: 'rec_1', sentSlices: 4, problem: { kind: 'serverDown' } })
    expect(captureViewOf(inputs({ latest })).kind).toBe('serverDown')
  })

  it('shows the words of a refusal, with the capture to send again', () => {
    const latest = aCapture({ problem: { kind: 'refused', text: 'The audio format is not known.' } })
    expect(captureViewOf(inputs({ latest }))).toEqual({ kind: 'refused', text: 'The audio format is not known.', clientRecordingId: 'c1' })
  })

  it('follows the Server row once the Server has all of the audio', () => {
    const latest = aCapture({ sessionId: 'ses_1', recordingId: 'rec_1', sentSlices: 10, isFinished: true })
    expect(captureViewOf(inputs({ latest }))).toEqual({ kind: 'transcribing', problemText: null })
    const waiting = aRecording({ status: 'Received', problemText: 'The speech model downloads.' })
    expect(captureViewOf(inputs({ latest, recording: waiting }))).toEqual({ kind: 'transcribing', problemText: 'The speech model downloads.' })
    expect(captureViewOf(inputs({ latest, recording: aRecording({ status: 'Transcribed' }) }))).toEqual({ kind: 'ready', sessionId: 'ses_1' })
    expect(captureViewOf(inputs({ latest, recording: aRecording({ status: 'Failed', problemText: 'No speech was found.' }) }))).toEqual({
      kind: 'failed',
      recordingId: 'rec_1',
      sessionId: 'ses_1',
      problemText: 'No speech was found.',
    })
  })

  it('goes back to the mic when the words of the last recording came long ago', () => {
    const latest = aCapture({ sessionId: 'ses_1', recordingId: 'rec_1', sentSlices: 10, isFinished: true })
    const recording = aRecording({ status: 'Transcribed' })
    expect(captureViewOf(inputs({ latest, recording, now: startedAt + 10_000 + readyShowsForMs + 1 })).kind).toBe('idle')
  })

  it('keeps a failure in view until the person acts, however old it is', () => {
    const latest = aCapture({ sessionId: 'ses_1', recordingId: 'rec_1', sentSlices: 10, isFinished: true })
    const recording = aRecording({ status: 'Failed' })
    expect(captureViewOf(inputs({ latest, recording, now: startedAt + 10 * readyShowsForMs })).kind).toBe('failed')
  })

  it('counts the other captures that still hold audio in this browser', () => {
    const captures = [aCapture({ clientRecordingId: 'a' }), aCapture({ clientRecordingId: 'b', isFinished: true }), aCapture({ clientRecordingId: 'c' }), aCapture({ clientRecordingId: 'd' })]
    expect(waitingBesides(captures)).toBe(2)
  })
})
