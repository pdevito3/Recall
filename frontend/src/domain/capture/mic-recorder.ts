import type { RecordingForCreationDtoFormatEnumKey } from '@/api/generated/models/types/recording-for-creation-dto'
import { newUuid } from '@/lib/new-uuid'

import { addCapture, endCapture, storeSlice, type StoredCapture } from './audio-store'
import type { MicFailure } from './capture-view'

/** The recorder hands over a slice each second, and each slice is stored before the next. */
export const sliceMs = 1_000

const recordingLockOf = (clientRecordingId: string) => `recall.capture.recording.${clientRecordingId}`

/** A browser with no Web Locks counts a capture as live while its slices keep coming. */
const liveWithNoLockMs = 5 * sliceMs

const formats: { format: RecordingForCreationDtoFormatEnumKey; mimeType: string }[] = [
  { format: 'Webm', mimeType: 'audio/webm;codecs=opus' },
  { format: 'Webm', mimeType: 'audio/webm' },
  // Older Safari records only MP4.
  { format: 'Mp4', mimeType: 'audio/mp4' },
]

export function recordingFormat() {
  if (typeof MediaRecorder === 'undefined' || !navigator.mediaDevices?.getUserMedia) return null
  return formats.find(({ mimeType }) => MediaRecorder.isTypeSupported(mimeType)) ?? null
}

export class MicStartError extends Error {
  readonly failure: MicFailure

  constructor(failure: MicFailure) {
    super(`The mic did not start: ${failure}`)
    this.failure = failure
  }
}

function failureOf(error: unknown): MicFailure {
  const name = error instanceof DOMException ? error.name : ''
  if (name === 'NotAllowedError' || name === 'SecurityError') return 'denied'
  if (name === 'NotFoundError' || name === 'OverconstrainedError') return 'noMic'
  if (name === 'NotReadableError' || name === 'AbortError') return 'micBusy'
  return 'micFailed'
}

/** Holds the Web Lock of a capture until `release`, so another tab knows that this tab still records it. */
async function holdRecordingLock(clientRecordingId: string): Promise<() => void> {
  if (!navigator.locks) return () => undefined
  let release = () => undefined as void
  const held = new Promise<void>((resolve) => (release = resolve))
  await new Promise<void>((granted) => {
    void navigator.locks.request(recordingLockOf(clientRecordingId), () => {
      granted()
      return held
    })
  })
  return release
}

const recordersOfThisTab = new Set<string>()

/** True while a tab of this browser records the capture. A capture of a closed tab is not live, and the queue ends it. */
export async function isRecordingLive(clientRecordingId: string, touchedAt: number): Promise<boolean> {
  if (recordersOfThisTab.has(clientRecordingId)) return true
  if (!navigator.locks) return Date.now() - touchedAt < liveWithNoLockMs
  const state = await navigator.locks.query()
  return state.held?.some((lock) => lock.name === recordingLockOf(clientRecordingId)) ?? false
}

export interface ActiveRecording {
  clientRecordingId: string
  startedAt: number
  /** The loudness now, from 0 to 1. */
  level: () => number
  stop: () => Promise<void>
  /** Settles when the recorder stops for any reason, such as a mic that goes away, and each slice is stored. */
  ended: Promise<void>
}

function levelMeterOf(stream: MediaStream) {
  try {
    const context = new AudioContext()
    const analyser = context.createAnalyser()
    analyser.fftSize = 512
    context.createMediaStreamSource(stream).connect(analyser)
    // A stop soon after the start closes the context before the resume ends.
    context.resume().catch(() => undefined)
    const samples = new Float32Array(analyser.fftSize)
    return {
      level: () => {
        analyser.getFloatTimeDomainData(samples)
        let sum = 0
        for (const sample of samples) sum += sample * sample
        // Speech near a phone mic has an RMS of about 0.05 to 0.3, so the square root spreads it over the meter.
        return Math.min(1, Math.sqrt(Math.sqrt(sum / samples.length)) * 1.6)
      },
      close: () => void context.close().catch(() => undefined),
    }
  } catch {
    return { level: () => 0, close: () => undefined }
  }
}

/**
 * Starts the mic and stores each slice in this browser as it comes. It answers when the recorder runs. Each slice is
 * written before the next one, so the store never holds a gap, and a failed write stops the recording with the slices
 * that are safe.
 */
export async function startRecording(owner: string, timeZone: StoredCapture['timeZone'], onSliceFailed: (error: unknown) => void): Promise<ActiveRecording> {
  const chosen = recordingFormat()
  if (!chosen) throw new MicStartError('unsupported')

  let stream: MediaStream
  try {
    stream = await navigator.mediaDevices.getUserMedia({ audio: true })
  } catch (error) {
    throw new MicStartError(failureOf(error))
  }

  const clientRecordingId = newUuid()
  const startedAt = Date.now()
  recordersOfThisTab.add(clientRecordingId)
  const releaseLock = holdRecordingLock(clientRecordingId)
  const stored = addCapture({
    clientRecordingId,
    owner,
    clientSessionId: newUuid(),
    startedAt: new Date(startedAt).toISOString(),
    timeZone,
    format: chosen.format,
    storedSlices: 0,
    storedBytes: 0,
    sentSlices: 0,
    durationMs: 0,
    isEnded: false,
    sessionId: null,
    recordingId: null,
    isFinished: false,
    problem: null,
    touchedAt: startedAt,
  })

  const recorder = new MediaRecorder(stream, { mimeType: chosen.mimeType })
  const meter = levelMeterOf(stream)
  let writes: Promise<unknown> = stored
  let nextIndex = 0
  let hasFailed = false

  recorder.ondataavailable = ({ data }) => {
    if (data.size === 0 || hasFailed) return
    const index = nextIndex++
    const durationMs = Date.now() - startedAt
    writes = writes
      .then(async () => storeSlice(clientRecordingId, index, await data.arrayBuffer(), durationMs))
      .catch((error: unknown) => {
        if (hasFailed) return
        hasFailed = true
        onSliceFailed(error)
        if (recorder.state !== 'inactive') recorder.stop()
      })
  }

  // A tab that goes to the background can close with no other event, so the part of the second that it holds is stored now.
  const storeWhatIsHeard = () => {
    if (document.visibilityState === 'hidden' && recorder.state === 'recording') recorder.requestData()
  }
  document.addEventListener('visibilitychange', storeWhatIsHeard)
  window.addEventListener('pagehide', storeWhatIsHeard)

  const ended = new Promise<void>((resolve) => {
    recorder.onstop = () => {
      stream.getTracks().forEach((track) => track.stop())
      meter.close()
      document.removeEventListener('visibilitychange', storeWhatIsHeard)
      window.removeEventListener('pagehide', storeWhatIsHeard)
      // The last slice comes before the stop event, so it is in the chain of writes.
      void writes
        .then(() => endCapture(clientRecordingId))
        .catch(() => undefined)
        .finally(() => {
          recordersOfThisTab.delete(clientRecordingId)
          void releaseLock.then((release) => release())
          resolve()
        })
    }
  })
  // A mic that goes away ends the tracks, and the recorder then stops by itself.
  stream.getAudioTracks().forEach((track) => track.addEventListener('ended', () => recorder.state !== 'inactive' && recorder.stop()))

  try {
    await new Promise<void>((started, failed) => {
      recorder.onstart = () => started()
      recorder.onerror = () => failed(new MicStartError('micFailed'))
      recorder.start(sliceMs)
    })
  } catch (error) {
    stream.getTracks().forEach((track) => track.stop())
    recorder.onstop?.(new Event('stop'))
    throw error instanceof MicStartError ? error : new MicStartError('micFailed')
  }
  recorder.onerror = () => {
    if (recorder.state !== 'inactive') recorder.stop()
  }

  return {
    clientRecordingId,
    startedAt,
    level: meter.level,
    stop: async () => {
      if (recorder.state !== 'inactive') recorder.stop()
      await ended
    },
    ended,
  }
}
