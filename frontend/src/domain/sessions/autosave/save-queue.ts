export interface Stamped<T> {
  value: T
  savedAt: string
}

export interface SaveQueue<T> {
  unsent: Stamped<T> | null
  sending: Stamped<T> | null
  unanswered: Stamped<T> | null
  isWaitingSinceNoReply: boolean
  refusal: { message: string; change: Stamped<T> | null } | null
  lastSavedAt: string | null
}

export type SaveEvent<T> =
  | { type: 'edited'; change: Stamped<T> }
  | { type: 'merged'; change: Stamped<T> }
  | { type: 'sent' }
  | { type: 'replied'; savedAt: string }
  | { type: 'noReply' }
  | { type: 'refused'; message: string }
  | { type: 'adopted'; queue: SaveQueue<T> }

export type SaveStatus =
  | { kind: 'idle' }
  | { kind: 'unsent' }
  | { kind: 'saving' }
  | { kind: 'waiting' }
  | { kind: 'saved'; at: string }
  | { kind: 'refused'; message: string }

export function emptyQueue<T>(): SaveQueue<T> {
  return { unsent: null, sending: null, unanswered: null, isWaitingSinceNoReply: false, refusal: null, lastSavedAt: null }
}

/** A change with no answer goes again first with its own `savedAt`, and `newer` goes after it. */
export function queueWithUnanswered<T>(change: Stamped<T>, newer: Stamped<T> | null = null): SaveQueue<T> {
  return { ...emptyQueue<T>(), unanswered: change, unsent: newer, isWaitingSinceNoReply: true }
}

export function queueWithRefusal<T>(change: Stamped<T>, message: string): SaveQueue<T> {
  return { ...emptyQueue<T>(), refusal: { message, change } }
}

export function nextToSend<T>(queue: SaveQueue<T>): Stamped<T> | null {
  return queue.sending ? null : (queue.unanswered ?? queue.unsent)
}

export function newestUnsettled<T>(queue: SaveQueue<T>): Stamped<T> | null {
  return queue.unsent ?? queue.unanswered ?? queue.sending ?? queue.refusal?.change ?? null
}

export function unsettledChanges<T>(queue: SaveQueue<T>): Stamped<T>[] {
  return [queue.refusal?.change ?? null, queue.sending, queue.unanswered, queue.unsent].filter((change) => change !== null)
}

export function step<T>(queue: SaveQueue<T>, event: SaveEvent<T>): SaveQueue<T> {
  switch (event.type) {
    case 'edited':
      return { ...queue, unsent: event.change, refusal: null }
    case 'merged':
      return { ...queue, unanswered: null, unsent: event.change, refusal: null }
    case 'sent': {
      const next = nextToSend(queue)
      if (!next) return queue
      return { ...queue, sending: next, unanswered: null, unsent: queue.unanswered ? queue.unsent : null }
    }
    case 'replied':
      return { ...queue, sending: null, isWaitingSinceNoReply: false, refusal: null, lastSavedAt: event.savedAt }
    case 'noReply':
      return { ...queue, sending: null, unanswered: queue.sending, isWaitingSinceNoReply: true }
    case 'refused':
      return {
        ...queue,
        sending: null,
        isWaitingSinceNoReply: false,
        refusal: { message: event.message, change: queue.sending },
      }
    case 'adopted':
      return { ...event.queue, lastSavedAt: event.queue.lastSavedAt ?? queue.lastSavedAt }
  }
}

export function statusOf<T>(queue: SaveQueue<T>): SaveStatus {
  if (queue.isWaitingSinceNoReply) return { kind: 'waiting' }
  if (queue.sending) return { kind: 'saving' }
  if (queue.unsent) return { kind: 'unsent' }
  if (queue.refusal !== null) return { kind: 'refused', message: queue.refusal.message }
  if (queue.lastSavedAt !== null) return { kind: 'saved', at: queue.lastSavedAt }
  return { kind: 'idle' }
}
