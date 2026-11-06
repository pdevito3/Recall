/** A change with the time that the client claims for it. A retry sends the same time, so the Server can see a replay. */
export interface Stamped<T> {
  value: T
  savedAt: string
}

/** The saves of one row. One save is on the wire at a time, and only the newest unsent change waits behind it. */
export interface SaveQueue<T> {
  unsent: Stamped<T> | null
  sending: Stamped<T> | null
  /** A save that got no reply. It can have reached the Server, so it goes again, with its own time, before a newer change. */
  unanswered: Stamped<T> | null
  /** True from a save with no reply until the next reply. A retry in this time does not flip the state on the screen. */
  isWaiting: boolean
  refusal: string | null
  lastSavedAt: string | null
}

export type SaveEvent<T> =
  | { type: 'edited'; change: Stamped<T> }
  | { type: 'sent' }
  | { type: 'replied'; savedAt: string }
  | { type: 'noReply' }
  | { type: 'refused'; message: string }

export type SaveStatus =
  | { kind: 'idle' }
  | { kind: 'unsent' }
  | { kind: 'saving' }
  | { kind: 'waiting' }
  | { kind: 'saved'; at: string }
  | { kind: 'refused'; message: string }

export function emptyQueue<T>(): SaveQueue<T> {
  return { unsent: null, sending: null, unanswered: null, isWaiting: false, refusal: null, lastSavedAt: null }
}

/** A queue that starts with a change from an earlier page load. That change can have reached the Server. */
export function queueWithUnanswered<T>(change: Stamped<T>): SaveQueue<T> {
  return { ...emptyQueue<T>(), unanswered: change, isWaiting: true }
}

export function nextToSend<T>(queue: SaveQueue<T>): Stamped<T> | null {
  return queue.sending ? null : (queue.unanswered ?? queue.unsent)
}

/** The newest change that the Server has not answered, or null when each change has a reply. */
export function newestUnsettled<T>(queue: SaveQueue<T>): Stamped<T> | null {
  return queue.unsent ?? queue.unanswered ?? queue.sending
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
      return { ...queue, sending: null, isWaiting: false, refusal: null, lastSavedAt: event.savedAt }
    case 'noReply':
      return { ...queue, sending: null, unanswered: queue.sending, isWaiting: true }
    case 'refused':
      return { ...queue, sending: null, isWaiting: false, refusal: event.message }
  }
}

export function statusOf<T>(queue: SaveQueue<T>): SaveStatus {
  if (queue.isWaiting) return { kind: 'waiting' }
  if (queue.sending) return { kind: 'saving' }
  if (queue.unsent) return { kind: 'unsent' }
  if (queue.refusal !== null) return { kind: 'refused', message: queue.refusal }
  if (queue.lastSavedAt !== null) return { kind: 'saved', at: queue.lastSavedAt }
  return { kind: 'idle' }
}
