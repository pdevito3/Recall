import type { Stamped } from './save-queue'

export interface OutboxEntry {
  key: string
  channel: 'raw' | 'cleaned'
  clientSessionId: string
  startedAt: string
  sessionId: string | null
  baseRevision: number
  latestRevision: number
  change: Stamped<string>
  refusal?: string | null
  movedFromDeletedEntry?: boolean
  isHold?: boolean
}

const ownerWithNoDot = (owner: string) => encodeURIComponent(owner).replaceAll('.', '%2E')

const prefixOf = (owner: string) => `recall.unsent.${ownerWithNoDot(owner)}.`

const holdSuffix = '.hold'

const isStamped = (value: unknown): value is Stamped<string> => {
  const change = value as Stamped<string> | null
  return typeof change?.value === 'string' && typeof change.savedAt === 'string'
}

function parseEntry(stored: string | null): OutboxEntry | null {
  try {
    const entry = JSON.parse(stored ?? 'null') as Partial<OutboxEntry> | null
    const isEntry = typeof entry?.key === 'string'
      && (entry.channel === 'raw' || entry.channel === 'cleaned')
      && typeof entry.clientSessionId === 'string'
      && typeof entry.startedAt === 'string'
      && (entry.sessionId === null || typeof entry.sessionId === 'string')
      && typeof entry.baseRevision === 'number'
      && typeof entry.latestRevision === 'number'
      && isStamped(entry.change)
    return isEntry ? (entry as OutboxEntry) : null
  } catch {
    return null
  }
}

/** Storage can be full or blocked, and then a reload loses the words. The false answer lets the page tell the person at once. */
export function writeToOutbox(owner: string, entry: OutboxEntry): boolean {
  try {
    localStorage.setItem(prefixOf(owner) + entry.key + (entry.isHold ? holdSuffix : ''), JSON.stringify(entry))
    return true
  } catch {
    return false
  }
}

export function removeFromOutbox(owner: string, key: string) {
  try {
    localStorage.removeItem(prefixOf(owner) + key)
  } catch {
    return
  }
}

export function removeHoldFromOutbox(owner: string, key: string) {
  removeFromOutbox(owner, key + holdSuffix)
}

export function readHoldFromOutbox(owner: string, key: string): OutboxEntry | null {
  return readOutboxEntry(owner, key + holdSuffix)
}

export function readOutboxEntry(owner: string, key: string): OutboxEntry | null {
  try {
    return parseEntry(localStorage.getItem(prefixOf(owner) + key))
  } catch {
    return null
  }
}

export function readOutbox(owner: string): OutboxEntry[] {
  try {
    return Object.keys(localStorage)
      .filter((key) => key.startsWith(prefixOf(owner)))
      .flatMap((key) => {
        const entry = parseEntry(localStorage.getItem(key))
        if (!entry) localStorage.removeItem(key)
        return entry ?? []
      })
  } catch {
    return []
  }
}
