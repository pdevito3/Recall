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
  | { type: 'sent' }
  | { type: 'replied'; savedAt: string }
  | { type: 'noReply' }
  | { type: 'refused'; message: string; isGone: boolean }

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

export function queueWithUnanswered<T>(change: Stamped<T>): SaveQueue<T> {
  return { ...emptyQueue<T>(), unanswered: change, isWaitingSinceNoReply: true }
}

export function nextToSend<T>(queue: SaveQueue<T>): Stamped<T> | null {
  return queue.sending ? null : (queue.unanswered ?? queue.unsent)
}

export function newestUnsettled<T>(queue: SaveQueue<T>): Stamped<T> | null {
  return queue.unsent ?? queue.unanswered ?? queue.sending ?? queue.refusal?.change ?? null
}

export function step<T>(queue: SaveQueue<T>, event: SaveEvent<T>): SaveQueue<T> {
  switch (event.type) {
    case 'edited':
      return { ...queue, unsent: event.change, refusal: null }
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
        refusal: { message: event.message, change: event.isGone ? null : queue.sending },
      }
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
