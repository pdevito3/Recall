import type { Stamped } from './save-queue'

export interface OutboxEntry {
  key: string
  channel: 'raw' | 'cleaned'
  clientSessionId: string
  startedAt: string
  sessionId: string | null
  baseRevision: number
  change: Stamped<string>
}

const prefixOf = (owner: string) => `recall.unsent.${owner}.`

// Storage can be full or blocked. The save still goes to the Server, so only a reload before the reply can lose words.
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

export function readOutbox(owner: string): OutboxEntry[] {
  try {
    return Object.keys(localStorage)
      .filter((key) => key.startsWith(prefixOf(owner)))
      .map((key) => JSON.parse(localStorage.getItem(key) ?? 'null') as OutboxEntry)
  } catch {
    return []
  }
}
