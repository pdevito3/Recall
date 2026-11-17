import type { RecordingForCreationDtoFormatEnumKey } from '@/api/generated/models/types/recording-for-creation-dto'
import { newUuid } from '@/lib/new-uuid'

import { addCapture, endCapture, storeParagraphTap, storeSlice, type StoredCapture } from './audio-store'
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

/**
 * Asks for the mic. A call-off answers at once, also while the person still reads the prompt of the browser, and a mic
 * that comes after it goes off at once.
 */
function micOf(callOff: AbortSignal | undefined): Promise<MediaStream> {
  return new Promise((resolve, reject) => {
    const calledOff = () => reject(callOff?.reason)
    if (callOff?.aborted) return calledOff()
    callOff?.addEventListener('abort', calledOff, { once: true })
    navigator.mediaDevices.getUserMedia({ audio: true }).then(
      (stream) => {
        callOff?.removeEventListener('abort', calledOff)
        if (callOff?.aborted) stream.getTracks().forEach((track) => track.stop())
        else resolve(stream)
      },
      (error: unknown) => {
        callOff?.removeEventListener('abort', calledOff)
        reject(new MicStartError(failureOf(error)))
      },
    )
  })
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
  /** Wall time, for the clock of the card and the time of the entry. It is not the clock of a tap. */
  startedAt: number
  /** The loudness now, from 0 to 1. */
  level: () => number
  stop: () => Promise<void>
  /** Settles when the recorder stops for any reason, such as a mic that goes away, and each slice is stored. */
  ended: Promise<void>
  /**
   * Marks a new paragraph at `pressedAt`, a `performance.now()` time from when the finger went down. Answers true when
   * this browser stored the tap, and false when the recorder no longer runs or the capture takes no more taps.
   */
  markParagraph: (pressedAt: number) => Promise<boolean>
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
 * that are safe. A call-off before the recorder runs rejects with its reason, and the mic is off and no audio is kept.
 */
export async function startRecording(owner: string, timeZone: StoredCapture['timeZone'], onSliceFailed: (error: unknown) => void, callOff?: AbortSignal): Promise<ActiveRecording> {
  const chosen = recordingFormat()
  if (!chosen) throw new MicStartError('unsupported')

  const stream = await micOf(callOff)

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
  // The zero of a tap. `startedAt` is taken before `start()`, so it leads the audio by the start of the recorder. The page
  // clock does not jump when the time of the system is set.
  let audioStartedAt = 0

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
      recorder.onstart = (event) => {
        audioStartedAt = event.timeStamp
        started()
      }
      recorder.onerror = () => failed(new MicStartError('micFailed'))
      recorder.start(sliceMs)
    })
  } catch (error) {
    stream.getTracks().forEach((track) => track.stop())
    recorder.onstop?.(new Event('stop'))
    throw error instanceof MicStartError ? error : new MicStartError('micFailed')
  }
  if (callOff?.aborted) {
    // The capture ends with no slice, and the upload queue removes it.
    recorder.ondataavailable = null
    recorder.stop()
    await ended
    throw callOff.reason
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
    markParagraph: (pressedAt) => {
      if (recorder.state !== 'recording' || hasFailed) return Promise.resolve(false)
      const atMs = Math.max(0, Math.round(pressedAt - audioStartedAt))
      // On the chain of writes, so the tap is in the row before `endCapture`, and the finish that the queue builds holds it.
      const previous = writes
      const tap = previous.then(() => storeParagraphTap(clientRecordingId, atMs))
      writes = tap.then(() => undefined, () => previous)
      return tap.then((row) => row !== null, () => false)
    },
  }
}
