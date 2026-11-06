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
}

const ownerWithNoDot = (owner: string) => encodeURIComponent(owner).replaceAll('.', '%2E')

const prefixOf = (owner: string) => `recall.unsent.${ownerWithNoDot(owner)}.`

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

// Storage can be full or blocked. The save still goes to the Server, so a reload before the reply can lose words, and so can a reload after a refusal.
export function writeToOutbox(owner: string, entry: OutboxEntry) {
  try {
    localStorage.setItem(prefixOf(owner) + entry.key, JSON.stringify(entry))
  } catch {
    return
  }
}

export function removeFromOutbox(owner: string, key: string) {
  try {
    localStorage.removeItem(prefixOf(owner) + key)
  } catch {
    return
  }
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
      .flatMap((key) => parseEntry(localStorage.getItem(key)) ?? [])
  } catch {
    return []
  }
}
