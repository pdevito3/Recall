import {
  CONCURRENT_CHANGE_PROBLEM_TYPE,
  isProblemDetailsError,
  PAGE_USER_MISMATCH_PROBLEM_TYPE,
  STALE_VERSION_PROBLEM_TYPE,
  type ProblemDetailsError,
} from '@/lib/problem-details'

import { emptyQueue, newestUnsettled, nextToSend, statusOf, step, unsettledChanges, type SaveEvent, type SaveQueue, type SaveStatus, type Stamped } from './save-queue'

const firstRetryMs = 2_000
const longestRetryMs = 30_000
const requestLimitMs = 30_000
const statusesThatCanChange = new Set([401, 408, 429])
const conflictsThatPass = new Set([STALE_VERSION_PROBLEM_TYPE, CONCURRENT_CHANGE_PROBLEM_TYPE, PAGE_USER_MISMATCH_PROBLEM_TYPE])

export function isARowThatIsGone(error: unknown): boolean {
  return isProblemDetailsError(error)
    && (error.status === 404 || error.status === 409)
    && !conflictsThatPass.has(error.problemDetails.type ?? '')
}

export function isFinalRefusal(error: unknown): error is ProblemDetailsError {
  if (!isProblemDetailsError(error) || conflictsThatPass.has(error.problemDetails.type ?? '')) return false
  const status = error.status ?? 500
  return status >= 400 && status < 500 && !statusesThatCanChange.has(status)
}

interface SerialSaverOptions<T, R> {
  debounceMs: number
  send: (change: Stamped<T>, signal: AbortSignal) => Promise<R>
  onReply: (reply: R, change: Stamped<T>) => void
  refusalMessage?: (error: ProblemDetailsError) => string
  /** A queue of partial changes keeps only the last change of a field, also when an older change had no reply. */
  mergeEdits?: (older: T, newer: T) => T
  onQueueChange?: (queue: SaveQueue<T>) => void
}

export class SerialSaver<T, R> {
  private queue: SaveQueue<T>
  private status: SaveStatus
  private readonly listeners = new Set<() => void>()
  private debounce: ReturnType<typeof setTimeout> | undefined
  private retry: ReturnType<typeof setTimeout> | undefined
  private hold: ReturnType<typeof setTimeout> | undefined
  private retryMs = firstRetryMs
  private isDisposed = false
  private isPaused = false
  /** A pause ends each request of the queue before it, so a later answer of such a request changes nothing. */
  private generation = 0
  private settleWaiters: ((isSettled: boolean) => void)[] = []
  private readonly options: SerialSaverOptions<T, R>

  constructor(options: SerialSaverOptions<T, R>) {
    this.options = options
    this.queue = emptyQueue<T>()
    this.status = statusOf(this.queue)
    window.addEventListener('online', this.retryNow)
  }

  /** The queue of another saver goes on here: a change with no answer goes again first, with its own `savedAt`. */
  adopt(queue: SaveQueue<T>) {
    if (this.isDisposed) return
    this.isPaused = false
    this.generation += 1
    this.apply({ type: 'adopted', queue })
    if (this.queue.unanswered) this.sendAfterThisRender()
  }

  /** The queue goes to another saver. Nothing goes from here until a next `adopt`, and an answer of a request from before changes nothing. */
  pause() {
    this.cancelDebounce()
    clearTimeout(this.retry)
    clearTimeout(this.hold)
    this.hold = undefined
    this.retryMs = firstRetryMs
    this.isPaused = true
    this.generation += 1
    this.apply({ type: 'adopted', queue: emptyQueue<T>() })
    this.answerSettleWaiters()
  }

  /** The queue comes back with nothing in it, as when no tab handed words over, so the next change goes from here. */
  resume() {
    this.isPaused = false
  }

  /** The change of a request with no answer yet, also after a request that did not answer and waits to go again. */
  inFlight(): Stamped<T> | null {
    return this.queue.sending ?? this.queue.unanswered
  }

  isSending() {
    return this.queue.sending !== null
  }

  /** A stamp from another tab stays with the words, because the Server compares the time at which the person typed them. */
  edit(value: T, savedAt = new Date().toISOString()) {
    const change = { value, savedAt }
    if (this.options.mergeEdits) this.apply({ type: 'merged', change: this.mergedWithWaiting(change) })
    else this.apply({ type: 'edited', change })
    this.cancelDebounce()
    if (this.options.debounceMs === 0) {
      this.pump()
      return
    }
    this.debounce = setTimeout(() => {
      this.debounce = undefined
      this.pump()
    }, this.options.debounceMs)
  }

  flush() {
    this.cancelDebounce()
    this.pump()
  }

  /** No change goes before the time is up, also on a flush. */
  holdFor(ms: number) {
    clearTimeout(this.hold)
    this.hold = setTimeout(() => {
      this.hold = undefined
      this.pump()
    }, ms)
  }

  dispose() {
    this.cancelDebounce()
    clearTimeout(this.retry)
    clearTimeout(this.hold)
    window.removeEventListener('online', this.retryNow)
    this.listeners.clear()
    this.isDisposed = true
    this.answerSettleWaiters()
  }

  settle(): Promise<boolean> {
    this.flush()
    return new Promise((resolve) => {
      this.settleWaiters.push(resolve)
      if (!this.queue.sending) this.answerSettleWaiters()
    })
  }

  newestUnsettledValue(): T | null {
    return newestUnsettled(this.queue)?.value ?? null
  }

  newestUnsettled() {
    return newestUnsettled(this.queue)
  }

  unsettled() {
    return unsettledChanges(this.queue)
  }

  refusalMessage() {
    return this.queue.refusal?.change === newestUnsettled(this.queue) ? (this.queue.refusal?.message ?? null) : null
  }

  subscribe = (listener: () => void) => {
    this.listeners.add(listener)
    return () => {
      this.listeners.delete(listener)
    }
  }

  getStatus = () => this.status

  private mergedWithWaiting(change: Stamped<T>): Stamped<T> {
    const { mergeEdits } = this.options
    const waiting = [this.queue.unanswered, this.queue.unsent].filter((older) => older !== null)
    if (!mergeEdits || waiting.length === 0) return change
    return { ...change, value: mergeEdits(waiting.reduce((older, newer) => ({ ...newer, value: mergeEdits(older.value, newer.value) })).value, change.value) }
  }

  private sendAfterThisRender() {
    this.retry = setTimeout(this.retryNow, 0)
  }

  private readonly retryNow = () => {
    clearTimeout(this.retry)
    this.pump()
  }

  private cancelDebounce() {
    clearTimeout(this.debounce)
    this.debounce = undefined
  }

  private apply(event: SaveEvent<T>) {
    this.queue = step(this.queue, event)
    this.status = statusOf(this.queue)
    this.options.onQueueChange?.(this.queue)
    for (const listener of this.listeners) listener()
  }

  private pump() {
    const change = nextToSend(this.queue)
    if (!change || this.isDisposed || this.isPaused || this.hold !== undefined) return
    const generation = this.generation
    this.apply({ type: 'sent' })
    // The write of the queue can learn that another tab took the entry, and then nothing goes.
    if (this.isDisposed || generation !== this.generation) return
    const limit = new AbortController()
    const limitTimer = setTimeout(() => limit.abort(), requestLimitMs)
    this.options.send(change, limit.signal).then(
      (reply) => {
        clearTimeout(limitTimer)
        if (generation === this.generation) this.handleReply(reply, change)
      },
      (error: unknown) => {
        clearTimeout(limitTimer)
        if (generation === this.generation) this.handleFailure(error)
      },
    )
  }

  private handleReply(reply: R, change: Stamped<T>) {
    if (this.isDisposed) return
    this.retryMs = firstRetryMs
    this.options.onReply(reply, change)
    this.apply({ type: 'replied', savedAt: change.savedAt })
    if (this.settleWaiters.length > 0) this.pump()
    else this.pumpUnlessDebouncing()
    if (!this.queue.sending) this.answerSettleWaiters()
  }

  private handleFailure(error: unknown) {
    if (this.isDisposed) return
    if (isFinalRefusal(error)) {
      this.apply({ type: 'refused', message: this.options.refusalMessage?.(error) ?? error.displayMessage })
      this.pumpUnlessDebouncing()
      if (!this.queue.sending) this.answerSettleWaiters()
      return
    }
    this.apply({ type: 'noReply' })
    if (this.options.mergeEdits && this.queue.unsent) this.apply({ type: 'merged', change: this.mergedWithWaiting({ ...this.queue.unsent, value: this.queue.unsent.value }) })
    clearTimeout(this.retry)
    this.retry = setTimeout(this.retryNow, this.retryMs)
    this.retryMs = Math.min(this.retryMs * 2, longestRetryMs)
    this.answerSettleWaiters()
  }

  private answerSettleWaiters() {
    const isSettled = newestUnsettled(this.queue) === null
    for (const answer of this.settleWaiters.splice(0)) answer(isSettled)
  }

  private pumpUnlessDebouncing() {
    if (this.debounce === undefined) this.pump()
  }
}
