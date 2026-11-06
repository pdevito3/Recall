import { isProblemDetailsError } from '@/lib/problem-details'

import { emptyQueue, newestUnsettled, nextToSend, statusOf, step, type SaveEvent, type SaveQueue, type SaveStatus, type Stamped } from './save-queue'

const firstRetryMs = 2_000
const longestRetryMs = 30_000
// A sign-in that ended, a timeout, and a rate limit are answers that a later try can change.
const statusesThatCanChange = new Set([401, 408, 429])

/** Null means that the save got no reply that settles it, so it goes again. */
function refusalOf(error: unknown): string | null {
  if (!isProblemDetailsError(error)) return null
  const status = error.status ?? 500
  const isRefused = status >= 400 && status < 500 && !statusesThatCanChange.has(status)
  return isRefused ? error.displayMessage : null
}

interface SerialSaverOptions<T, R> {
  debounceMs: number
  send: (change: Stamped<T>) => Promise<R>
  onReply: (reply: R, change: Stamped<T>) => void
  initialQueue?: SaveQueue<T>
  onQueueChange?: (queue: SaveQueue<T>) => void
}

/**
 * Sends the changes of one row in order, one at a time, and waits for each reply. Metadata, settings, and drafts follow
 * this rule of the API (docs/specs/sessions.md). It has no React in it, so a save goes on after the page that made it closes.
 */
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
    if (this.queue.unanswered) this.retryNow()
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

  /** Sends the newest change now, with no wait for the pause in typing. */
  flush() {
    this.cancelDebounce()
    this.pump()
  }

  /** Stops each timer and each listener. A save that is on the wire still finishes, and its reply changes nothing. */
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
    const refusal = refusalOf(error)
    if (refusal !== null) {
      this.apply({ type: 'refused', message: refusal })
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
