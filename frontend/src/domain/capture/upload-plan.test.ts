import { describe, expect, it } from 'vitest'

import type { StoredCapture } from './audio-store'
import { finishedCapturesToForget, nextCaptureToSend, nextUploadStep, retryDelayMs, verdictOf, type UploadStep } from './upload-plan'

const aCapture = (changes: Partial<StoredCapture> = {}): StoredCapture => ({
  clientRecordingId: 'c1',
  owner: 'ada',
  clientSessionId: 's1',
  startedAt: '2026-11-10T08:00:00.000Z',
  format: 'Webm',
  storedSlices: 3,
  storedBytes: 3_000,
  sentSlices: 0,
  durationMs: 3_000,
  isEnded: false,
  sessionId: null,
  recordingId: null,
  isFinished: false,
  problem: null,
  touchedAt: 0,
  ...changes,
})

describe('the next upload step', () => {
  it('makes the entry, then the recording, then sends each slice in order, then finishes with the count and the bytes', () => {
    expect(nextUploadStep(aCapture())).toEqual({ kind: 'createEntry' })
    expect(nextUploadStep(aCapture({ sessionId: 'ses_1' }))).toEqual({ kind: 'createRecording' })
    expect(nextUploadStep(aCapture({ sessionId: 'ses_1', recordingId: 'rec_1', sentSlices: 1 }))).toEqual({ kind: 'sendSlice', index: 1 })
    expect(nextUploadStep(aCapture({ sessionId: 'ses_1', recordingId: 'rec_1', sentSlices: 3, isEnded: true }))).toEqual({ kind: 'finish', sliceCount: 3, byteLength: 3_000 })
  })

  it('sends slices while the recorder runs, and finishes only after the recorder stops', () => {
    expect(nextUploadStep(aCapture({ sessionId: 'ses_1', recordingId: 'rec_1', sentSlices: 3 }))).toEqual({ kind: 'waitForSlices' })
  })

  it('makes no entry before the first slice, so a tap with no audio leaves no empty entry', () => {
    expect(nextUploadStep(aCapture({ storedSlices: 0, storedBytes: 0 }))).toEqual({ kind: 'waitForSlices' })
    expect(nextUploadStep(aCapture({ storedSlices: 0, storedBytes: 0, isEnded: true }))).toEqual({ kind: 'discard' })
  })

  it('has nothing to do after the finish', () => {
    expect(nextUploadStep(aCapture({ isFinished: true }))).toEqual({ kind: 'done' })
  })
})

describe('a failed step', () => {
  const send: UploadStep = { kind: 'sendSlice', index: 2 }
  const failure = (status: number | null, isOnline = true) => ({ status, text: 'The Server refused it.', isOnline })

  it('waits and tries again when no answer came, and names the network when the browser is offline', () => {
    expect(verdictOf(send, failure(null))).toEqual({ kind: 'retry', problem: { kind: 'serverDown' } })
    expect(verdictOf(send, failure(null, false))).toEqual({ kind: 'retry', problem: { kind: 'offline' } })
  })

  it('waits and tries again on an answer that can change by itself', () => {
    for (const status of [401, 408, 429, 500, 502, 503, 507]) {
      expect(verdictOf(send, failure(status)).kind).toBe('retry')
    }
  })

  it('moves the audio to a new entry when the person deleted the entry before its audio came', () => {
    expect(verdictOf({ kind: 'createEntry' }, failure(409))).toEqual({ kind: 'newEntry' })
    expect(verdictOf({ kind: 'createRecording' }, failure(404))).toEqual({ kind: 'newEntry' })
  })

  it('lets the audio go when the person deleted the entry, and its recording, during the upload', () => {
    expect(verdictOf(send, failure(404))).toEqual({ kind: 'gone' })
    expect(verdictOf({ kind: 'finish', sliceCount: 3, byteLength: 3 }, failure(404))).toEqual({ kind: 'gone' })
  })

  it('keeps the audio and shows the words of any other refusal', () => {
    expect(verdictOf(send, failure(422))).toEqual({ kind: 'refused', problem: { kind: 'refused', text: 'The Server refused it.' } })
    expect(verdictOf(send, failure(409))).toEqual({ kind: 'refused', problem: { kind: 'refused', text: 'The Server refused it.' } })
  })
})

describe('the wait before a next try', () => {
  it('starts at 1 s and doubles up to 30 s', () => {
    expect([1, 2, 3, 4, 5, 6, 7].map((failures) => retryDelayMs(failures, 0))).toEqual([1_000, 2_000, 4_000, 8_000, 16_000, 30_000, 30_000])
  })

  it('adds up to half of the wait at random', () => {
    expect(retryDelayMs(1, 1)).toBe(1_500)
    expect(retryDelayMs(10, 1)).toBe(45_000)
  })
})

describe('the queue', () => {
  it('sends the oldest capture with work first, and skips one that waits for the person', () => {
    const refused = aCapture({ clientRecordingId: 'a', problem: { kind: 'refused', text: 'No.' } })
    const live = aCapture({ clientRecordingId: 'b', sessionId: 'ses_1', recordingId: 'rec_1', sentSlices: 3 })
    const ended = aCapture({ clientRecordingId: 'c', isEnded: true })
    expect(nextCaptureToSend([refused, live, ended])?.clientRecordingId).toBe('c')
    expect(nextCaptureToSend([refused, live])).toBeNull()
  })

  it('forgets each finished capture but the newest, which the card follows', () => {
    const old = aCapture({ clientRecordingId: 'a', isFinished: true })
    const waiting = aCapture({ clientRecordingId: 'b' })
    const newest = aCapture({ clientRecordingId: 'c', isFinished: true })
    expect(finishedCapturesToForget([old, waiting, newest]).map((capture) => capture.clientRecordingId)).toEqual(['a'])
  })
})
