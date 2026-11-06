import { newUuid } from '@/lib/new-uuid'

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

export interface HeldSlot {
  slot: string
  entry: OutboxEntry
}

const ownerWithNoDot = (owner: string) => encodeURIComponent(owner).replaceAll('.', '%2E')

const prefixOf = (owner: string) => `recall.unsent.${ownerWithNoDot(owner)}.`

const holdMark = '.hold.'

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
    localStorage.setItem(prefixOf(owner) + entry.key, JSON.stringify(entry))
    return true
  } catch {
    return false
  }
}

/** Each tab has its own slot, so that a tab cannot write over the held words of another tab. */
export function writeHoldToOutbox(owner: string, entry: OutboxEntry, slot: string): boolean {
  try {
    localStorage.setItem(prefixOf(owner) + entry.key + holdMark + slot, JSON.stringify(entry))
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

export function removeHoldsFromOutbox(owner: string, key: string, slots: string[]) {
  for (const slot of slots) removeFromOutbox(owner, key + holdMark + slot)
}

export function readHoldsFromOutbox(owner: string, key: string): HeldSlot[] {
  try {
    const start = prefixOf(owner) + key + holdMark
    return Object.keys(localStorage)
      .filter((stored) => stored.startsWith(start))
      .flatMap((stored) => {
        const entry = parseEntry(localStorage.getItem(stored))
        return entry ? { slot: stored.slice(start.length), entry } : []
      })
  } catch {
    return []
  }
}

export function readOutboxEntry(owner: string, key: string): OutboxEntry | null {
  try {
    return parseEntry(localStorage.getItem(prefixOf(owner) + key))
  } catch {
    return null
  }
}

function changeIn(stored: string | null): Stamped<string> | null {
  try {
    const { change } = JSON.parse(stored ?? 'null') as Partial<OutboxEntry>
    return isStamped(change) ? change : null
  } catch {
    return null
  }
}

/** An entry that this version cannot read can still hold words, and those go on as a new entry. Only an entry with no words goes. */
function entryFromWordsOf(owner: string, stored: string | null): OutboxEntry | null {
  const change = changeIn(stored)
  if (!change?.value) return null
  const clientSessionId = newUuid()
  const entry: OutboxEntry = {
    key: `${clientSessionId}.raw`,
    channel: 'raw',
    clientSessionId,
    startedAt: change.savedAt,
    sessionId: null,
    baseRevision: 0,
    latestRevision: 0,
    change,
  }
  return writeToOutbox(owner, entry) ? entry : null
}

export function readOutbox(owner: string): OutboxEntry[] {
  try {
    return Object.keys(localStorage)
      .filter((key) => key.startsWith(prefixOf(owner)))
      .flatMap((key) => {
        const stored = localStorage.getItem(key)
        const entry = parseEntry(stored)
        if (entry) return entry
        const repaired = entryFromWordsOf(owner, stored)
        if (repaired || !changeIn(stored)?.value) localStorage.removeItem(key)
        return repaired ?? []
      })
  } catch {
    return []
  }
}
