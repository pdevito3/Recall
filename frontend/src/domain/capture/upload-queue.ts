import { onlineManager, type QueryClient } from '@tanstack/react-query'

import { addRecording } from '@/api/generated/recordings/clients/add-recording'
import { finishRecording } from '@/api/generated/recordings/clients/finish-recording'
import { storeRecordingSlice } from '@/api/generated/recordings/clients/store-recording-slice'
import { addSession } from '@/api/generated/sessions/clients/add-session'
import { refreshTimelineHead } from '@/domain/sessions/apis/timeline-cache'
import { newUuid } from '@/lib/new-uuid'
import { isProblemDetailsError } from '@/lib/problem-details'

import { CaptureKeys } from './apis/capture.keys'
import { capturesOf, confirmSlice, endCapture, readSlice, removeCapture, subscribeToAudioStore, updateCapture, type StoredCapture } from './audio-store'
import { isRecordingLive } from './mic-recorder'
import { finishedCapturesToForget, nextCaptureToSend, nextUploadStep, retryDelayMs, verdictOf, type UploadStep } from './upload-plan'

/** A call that hangs holds the queue, so each one ends after this long and goes again. */
const callLimitMs = 60_000

const uploadLockOf = (owner: string) => `recall.capture.upload.${owner}`

const failureOf = (error: unknown) => ({
  status: isProblemDetailsError(error) ? (error.status ?? null) : null,
  text: error instanceof Error ? error.message : 'The Server refused the audio.',
  isOnline: onlineManager.isOnline(),
})

const callOptions = () => ({ signal: AbortSignal.timeout(callLimitMs), options: { suppressErrorToast: true } })

async function take(capture: StoredCapture, step: UploadStep, queryClient: QueryClient) {
  const id = capture.clientRecordingId
  switch (step.kind) {
    case 'createEntry': {
      const created = await addSession({ body: { clientSessionId: capture.clientSessionId, startedAt: capture.startedAt }, ...callOptions() })
      await updateCapture(id, { sessionId: created.id, problem: null })
      void refreshTimelineHead(queryClient)
      return
    }
    case 'createRecording': {
      const recording = await addRecording({ path: { id: capture.sessionId! }, body: { clientRecordingId: id, format: capture.format }, ...callOptions() })
      await updateCapture(id, { recordingId: recording.id, problem: null })
      return
    }
    case 'sendSlice': {
      const bytes = await readSlice(id, step.index)
      if (!bytes) throw new Error(`Slice ${step.index} is not in this browser.`)
      await storeRecordingSlice({ path: { id: capture.recordingId!, index: step.index }, body: new Blob([bytes]), ...callOptions() })
      await confirmSlice(id, step.index)
      return
    }
    case 'finish': {
      const recording = await finishRecording({ path: { id: capture.recordingId! }, body: { sliceCount: step.sliceCount, byteLength: BigInt(step.byteLength) }, ...callOptions() })
      queryClient.setQueryData(CaptureKeys.recording(recording.id), recording)
      await updateCapture(id, { isFinished: true, problem: null })
      return
    }
    case 'discard':
      await removeCapture(id)
      return
    case 'waitForSlices':
    case 'done':
      return
  }
}

/**
 * Sends the stored audio of one person, one call at a time, oldest capture first. One tab of the browser sends at a time,
 * under a Web Lock, and the others follow the store. A failure waits 1 s, then twice as long each time up to 30 s, and the
 * return of the network or a new slice tries at once.
 */
class UploadQueue {
  private failures = 0
  /** News that came while the queue worked, so that the next pause does not wait for news that already came. */
  private hasNews = false
  private wake = () => {
    this.hasNews = true
  }
  /** While the queue waits after a failure, a new slice does not wake it, or a dead server would get a call each second. */
  private isWaitingAfterFailure = false
  private isStopped = false
  private stopWatching: (() => void)[] = []

  private readonly owner: string
  private readonly queryClient: QueryClient

  constructor(owner: string, queryClient: QueryClient) {
    this.owner = owner
    this.queryClient = queryClient
  }

  start() {
    this.stopWatching.push(
      subscribeToAudioStore(() => {
        if (!this.isWaitingAfterFailure) this.wake()
      }),
      onlineManager.subscribe((isOnline) => {
        if (!isOnline) return
        this.failures = 0
        this.wake()
      }),
    )
    void this.run()
  }

  stop() {
    this.isStopped = true
    this.stopWatching.forEach((stop) => stop())
    this.wake()
  }

  /** Clears a refusal, so the queue sends the capture again. */
  async tryAgain(clientRecordingId: string) {
    this.failures = 0
    await updateCapture(clientRecordingId, { problem: null })
    this.wake()
  }

  private async waitAfterFailure() {
    // A slice that came during the failed call is no reason to call the server again at once.
    this.hasNews = false
    this.isWaitingAfterFailure = true
    await this.pause(retryDelayMs(++this.failures, Math.random()))
    this.isWaitingAfterFailure = false
  }

  private async pause(ms: number | null) {
    if (this.hasNews || this.isStopped) {
      this.hasNews = false
      return
    }
    await new Promise<void>((resolve) => {
      const timer = ms === null ? undefined : setTimeout(resolve, ms)
      this.wake = () => {
        clearTimeout(timer)
        resolve()
      }
    })
    this.wake = () => {
      this.hasNews = true
    }
  }

  /** Reads the store, ends the captures of closed tabs, forgets old finished ones, and gives the capture to send next. */
  private async nextToSend() {
    const captures = await capturesOf(this.owner)
    for (const capture of captures) {
      if (!capture.isEnded && !(await isRecordingLive(capture.clientRecordingId, capture.touchedAt))) await endCapture(capture.clientRecordingId)
    }
    for (const old of finishedCapturesToForget(captures)) await removeCapture(old.clientRecordingId)
    return nextCaptureToSend(await capturesOf(this.owner))
  }

  private async run() {
    while (!this.isStopped) {
      let capture: StoredCapture | null
      try {
        capture = await this.nextToSend()
      } catch {
        await this.waitAfterFailure()
        continue
      }
      if (!capture) {
        await this.pause(null)
        continue
      }
      if (!onlineManager.isOnline()) {
        if (capture.problem?.kind !== 'offline') await updateCapture(capture.clientRecordingId, { problem: { kind: 'offline' } }).catch(() => undefined)
        await this.pause(null)
        continue
      }
      const outcome = await this.underUploadLock(() => this.sendAll())
      if (outcome === 'otherTabSends') await this.pause(otherTabSendsMs)
      else if (outcome === 'failed') await this.waitAfterFailure()
    }
  }

  /**
   * Holds the upload lock only while there is audio to send. A page that holds a Web Lock cannot go into the back-forward
   * cache of the browser, so a lock for the life of the page would load each page again on Back.
   */
  private async underUploadLock(work: () => Promise<SendOutcome>): Promise<SendOutcome | 'otherTabSends'> {
    if (!navigator.locks) return work()
    return navigator.locks.request(uploadLockOf(this.owner), { ifAvailable: true }, (lock) => (lock ? work() : 'otherTabSends'))
  }

  private async sendAll(): Promise<SendOutcome> {
    while (!this.isStopped && onlineManager.isOnline()) {
      const capture = await this.nextToSend().catch(() => undefined)
      if (capture === undefined) return 'failed'
      if (!capture) return 'sent'
      if (!(await this.send(capture))) return 'failed'
    }
    return 'sent'
  }

  /** Takes the next step of the capture, and answers false when the step must wait before it goes again. */
  private async send(capture: StoredCapture) {
    const step = nextUploadStep(capture)
    try {
      await take(capture, step, this.queryClient)
      this.failures = 0
      return true
    } catch (error) {
      const verdict = verdictOf(step, failureOf(error))
      try {
        if (verdict.kind === 'newEntry') await updateCapture(capture.clientRecordingId, { clientSessionId: newUuid(), sessionId: null, recordingId: null })
        else if (verdict.kind === 'gone') await removeCapture(capture.clientRecordingId)
        else await updateCapture(capture.clientRecordingId, { problem: verdict.problem })
      } catch {
        // The store refused the note, and the next round reads the capture again.
      }
      return verdict.kind !== 'retry'
    }
  }
}

type SendOutcome = 'sent' | 'failed'

/** Another tab sends. If it closes, its lock goes with no news to this tab, so this tab looks again after this long. */
const otherTabSendsMs = 5_000

let current: UploadQueue | null = null

/** Starts the queue of the person of this page, and stops the queue of an earlier person. */
export function sendStoredAudioOf(owner: string, queryClient: QueryClient): () => void {
  current?.stop()
  const queue = new UploadQueue(owner, queryClient)
  current = queue
  queue.start()
  return () => {
    queue.stop()
    if (current === queue) current = null
  }
}

export const tryAgainToSend = (clientRecordingId: string) => current?.tryAgain(clientRecordingId)
