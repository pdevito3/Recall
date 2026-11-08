import { newUuid } from '@/lib/new-uuid'

import type { Stamped } from './save-queue'
import { keysUnder, removeKey, writeKeyOfThisTab, type KeyOfATab } from './tab-keys'

export interface OutboxEntry {
  key: string
  channel: 'raw' | 'cleaned'
  clientSessionId: string
  startedAt: string
  sessionId: string | null
  baseRevision: number
  /** The text of the Draft at `baseRevision`. The tab that takes the words merges only the change of the person since this text. */
  baseText: string
  /** The newest words of the person. */
  change: Stamped<string> | null
  /** A change older than `change` that went to the Server with no answer yet. It goes again first, with its own `savedAt`, so a save that won answers `Kept`. */
  sent?: Stamped<string> | null
  /** The last save was `Lost`, so the next send reads the Draft first. */
  readsFirst?: boolean
  /** Names this state of the words, so a tab takes a hand-over of the same state once. */
  stateId?: string
  /** The states that this state took in. A late hand-over of one of them holds no word that this state lacks, so it goes nowhere. */
  takenStates?: string[]
  held?: string | null
  /** The words of a save that the Draft does not hold. They are safe in History, and this keeps the notice after a reload. */
  wordsInHistory?: string | null
  refusal?: string | null
  movedFromDeletedEntry?: boolean
}

export interface StoredEntry {
  storageKey: string
  writer: string | null
  entry: OutboxEntry
}

const ownerWithNoDot = (owner: string) => encodeURIComponent(owner).replaceAll('.', '%2E')

export const prefixOfOutbox = (owner: string) => `recall.unsent.${ownerWithNoDot(owner)}.`

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
      && typeof entry.baseText === 'string'
      && (entry.sent == null || isStamped(entry.sent))
      && (entry.takenStates === undefined || (Array.isArray(entry.takenStates) && entry.takenStates.every((state) => typeof state === 'string')))
      && (isStamped(entry.change) || ((typeof entry.held === 'string' || typeof entry.wordsInHistory === 'string') && (entry.change ?? null) === null))
    return isEntry ? (entry as OutboxEntry) : null
  } catch {
    return null
  }
}

/** The key of the written text. Storage can be full or blocked, and then a reload loses the words: the null answer lets the page tell the person at once. */
export const writeToOutbox = (owner: string, previousKey: string | null, entry: OutboxEntry) =>
  writeKeyOfThisTab(prefixOfOutbox(owner), previousKey, JSON.stringify(entry))

export const removeFromOutbox = removeKey

function changeIn(stored: string | null): Stamped<string> | null {
  try {
    const { change } = JSON.parse(stored ?? 'null') as Partial<OutboxEntry>
    return isStamped(change) ? change : null
  } catch {
    return null
  }
}

/** An entry that this version cannot read can still hold words, and those go on as a new entry. */
function entryFromWordsOf(stored: string | null): OutboxEntry | null {
  const change = changeIn(stored)
  if (!change?.value) return null
  const clientSessionId = newUuid()
  return { key: `${clientSessionId}.raw`, channel: 'raw', clientSessionId, startedAt: change.savedAt, sessionId: null, baseRevision: 0, baseText: '', change }
}

const entryIn = ({ storageKey, writer, stored }: KeyOfATab): StoredEntry | null => {
  const parsed = parseEntry(stored)
  if (parsed) return { storageKey, writer, entry: parsed }
  const repaired = entryFromWordsOf(stored)
  // A key with no words holds nothing to keep.
  if (!repaired) removeKey(storageKey)
  return repaired && { storageKey, writer: null, entry: repaired }
}

export const storedEntriesIn = (keys: KeyOfATab[]): StoredEntry[] => keys.flatMap((key) => entryIn(key) ?? [])

export const readOutbox = (owner: string) => storedEntriesIn(keysUnder(prefixOfOutbox(owner)))
