import { CONCURRENT_CHANGE_PROBLEM_TYPE, isProblemDetailsError, STALE_VERSION_PROBLEM_TYPE, type ProblemDetailsError } from '@/lib/problem-details'

import { emptyQueue, newestUnsettled, nextToSend, statusOf, step, type SaveEvent, type SaveQueue, type SaveStatus, type Stamped } from './save-queue'

const firstRetryMs = 2_000
const longestRetryMs = 30_000
const statusesThatCanChange = new Set([401, 408, 429])
const conflictsThatPass = new Set([STALE_VERSION_PROBLEM_TYPE, CONCURRENT_CHANGE_PROBLEM_TYPE])
const statusOfARowThatIsGone = 404

function isFinalRefusal(error: unknown): error is ProblemDetailsError {
  if (!isProblemDetailsError(error) || conflictsThatPass.has(error.problemDetails.type ?? '')) return false
  const status = error.status ?? 500
  return status >= 400 && status < 500 && !statusesThatCanChange.has(status)
}

interface SerialSaverOptions<T, R> {
  debounceMs: number
  send: (change: Stamped<T>) => Promise<R>
  onReply: (reply: R, change: Stamped<T>) => void
  initialQueue?: SaveQueue<T>
  onQueueChange?: (queue: SaveQueue<T>) => void
}

export class SerialSaver<T, R> {
  private queue: SaveQueue<T>
  private status: SaveStatus
  private readonly listeners = new Set<() => void>()
  private debounce: ReturnType<typeof setTimeout> | undefined
  private retry: ReturnType<typeof setTimeout> | undefined
  private retryMs = firstRetryMs
  private isDisposed = false
  private readonly options: SerialSaverOptions<T, R>

  constructor(options: SerialSaverOptions<T, R>) {
    this.options = options
    this.queue = options.initialQueue ?? emptyQueue<T>()
    this.status = statusOf(this.queue)
    window.addEventListener('online', this.retryNow)
    if (this.queue.unanswered) this.sendAfterThisRender()
  }

  edit(value: T) {
    this.apply({ type: 'edited', change: { value, savedAt: new Date().toISOString() } })
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

  dispose() {
    this.cancelDebounce()
    clearTimeout(this.retry)
    window.removeEventListener('online', this.retryNow)
    this.listeners.clear()
    this.isDisposed = true
  }

  newestUnsettledValue(): T | null {
    return newestUnsettled(this.queue)?.value ?? null
  }

  subscribe = (listener: () => void) => {
    this.listeners.add(listener)
    return () => {
      this.listeners.delete(listener)
    }
  }

  getStatus = () => this.status

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
    if (!change || this.isDisposed) return
    this.apply({ type: 'sent' })
    this.options.send(change).then(
      (reply) => this.handleReply(reply, change),
      (error: unknown) => this.handleFailure(error),
    )
  }

  private handleReply(reply: R, change: Stamped<T>) {
    if (this.isDisposed) return
    this.retryMs = firstRetryMs
    this.apply({ type: 'replied', savedAt: change.savedAt })
    this.options.onReply(reply, change)
    this.pumpUnlessDebouncing()
  }

  private handleFailure(error: unknown) {
    if (this.isDisposed) return
    if (isFinalRefusal(error)) {
      this.apply({ type: 'refused', message: error.displayMessage, isGone: error.status === statusOfARowThatIsGone })
      this.pumpUnlessDebouncing()
      return
    }
    this.apply({ type: 'noReply' })
    clearTimeout(this.retry)
    this.retry = setTimeout(this.retryNow, this.retryMs)
    this.retryMs = Math.min(this.retryMs * 2, longestRetryMs)
  }

  private pumpUnlessDebouncing() {
    if (this.debounce === undefined) this.pump()
  }
}
