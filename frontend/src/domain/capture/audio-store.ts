import type { RecordingForCreationDtoFormatEnumKey } from '@/api/generated/models/types/recording-for-creation-dto'
import { openChannel } from '@/lib/browser-channel'

/** Why the last upload step of a capture did not work. The audio stays in this browser in each case. */
export type UploadProblem = { kind: 'offline' } | { kind: 'serverDown' } | { kind: 'refused'; text: string }

/**
 * One recording of this browser, from the tap to the finish on the Server. Slices `sentSlices` to `storedSlices - 1` wait
 * here. A slice leaves only after the Server confirms it, so the audio is in this browser or on the Server at each moment.
 */
export interface StoredCapture {
  clientRecordingId: string
  /** The `sub` of the person who recorded. Only a page of that person sends the audio. */
  owner: string
  /** The new entry of this recording. It changes only when the Server says that the person deleted the entry before its first slice. */
  clientSessionId: string
  startedAt: string
  format: RecordingForCreationDtoFormatEnumKey
  storedSlices: number
  storedBytes: number
  sentSlices: number
  durationMs: number
  /** No slice comes after `storedSlices`: the recorder stopped, or its tab closed. */
  isEnded: boolean
  sessionId: string | null
  recordingId: string | null
  /** The Server has all of the audio. Only these fields stay, for the card to follow the text. */
  isFinished: boolean
  problem: UploadProblem | null
  touchedAt: number
}

interface StoredSlice {
  clientRecordingId: string
  index: number
  bytes: ArrayBuffer
}

const databaseName = 'recall.capture'
const captureStore = 'captures'
const sliceStore = 'slices'
const changeChannelName = 'recall.capture'

let opening: Promise<IDBDatabase> | null = null

function openDatabase(): Promise<IDBDatabase> {
  opening ??= new Promise<IDBDatabase>((resolve, reject) => {
    const request = indexedDB.open(databaseName, 1)
    request.onupgradeneeded = () => {
      request.result.createObjectStore(captureStore, { keyPath: 'clientRecordingId' })
      request.result.createObjectStore(sliceStore, { keyPath: ['clientRecordingId', 'index'] })
    }
    request.onsuccess = () => {
      const database = request.result
      // A newer version of the app in another tab upgrades the database, and this tab must let it.
      database.onversionchange = () => {
        database.close()
        opening = null
      }
      resolve(database)
    }
    request.onerror = () => reject(request.error ?? new Error('The audio store of this browser did not open.'))
  }).catch((error: unknown) => {
    opening = null
    throw error
  })
  return opening
}

const answerOf = <T>(request: IDBRequest<T>) =>
  new Promise<T>((resolve, reject) => {
    request.onsuccess = () => resolve(request.result)
    request.onerror = () => reject(request.error ?? new Error('A read of the audio store failed.'))
  })

interface Stores {
  captures: IDBObjectStore
  slices: IDBObjectStore
}

async function inTransaction<T>(mode: IDBTransactionMode, work: (stores: Stores) => Promise<T>, durability: IDBTransactionDurability = 'default'): Promise<T> {
  const database = await openDatabase()
  const transaction = database.transaction([captureStore, sliceStore], mode, { durability })
  const committed = new Promise<void>((resolve, reject) => {
    transaction.oncomplete = () => resolve()
    transaction.onerror = () => reject(transaction.error ?? new Error('A write to the audio store failed.'))
    transaction.onabort = () => reject(transaction.error ?? new Error('A write to the audio store stopped.'))
  })
  try {
    const result = await work({ captures: transaction.objectStore(captureStore), slices: transaction.objectStore(sliceStore) })
    await committed
    return result
  } catch (error) {
    if (!transaction.error) {
      try {
        transaction.abort()
      } catch {
        // The transaction already ended.
      }
    }
    committed.catch(() => undefined)
    throw error
  }
}

const listeners = new Set<() => void>()
const otherTabs = openChannel(changeChannelName)
otherTabs?.addEventListener('message', () => listeners.forEach((listener) => listener()))

function announceChange() {
  listeners.forEach((listener) => listener())
  otherTabs?.postMessage('changed')
}

/** Calls `listener` after each change of the store, in this tab or in another tab of this browser. */
export function subscribeToAudioStore(listener: () => void): () => void {
  listeners.add(listener)
  return () => listeners.delete(listener)
}

async function changeCapture(clientRecordingId: string, change: (capture: StoredCapture) => StoredCapture | null): Promise<StoredCapture | null> {
  const changed = await inTransaction('readwrite', async ({ captures }) => {
    const current = await answerOf<StoredCapture | undefined>(captures.get(clientRecordingId))
    if (!current) return null
    const next = change(current)
    if (next) await answerOf(captures.put(next))
    return next
  })
  announceChange()
  return changed
}

export async function addCapture(capture: StoredCapture): Promise<void> {
  await inTransaction('readwrite', async ({ captures }) => {
    await answerOf(captures.add(capture))
  })
  announceChange()
}

/** The captures of one person, oldest first. */
export async function capturesOf(owner: string): Promise<StoredCapture[]> {
  const all = await inTransaction('readonly', ({ captures }) => answerOf<StoredCapture[]>(captures.getAll()))
  return all.filter((capture) => capture.owner === owner).sort((a, b) => a.startedAt.localeCompare(b.startedAt))
}

/**
 * Writes the slice and counts it in one transaction, so a count never names a slice that is not here. The write is
 * strict: the browser puts it on disk before it answers, because a closed tab must not lose a second that the card showed as saved.
 */
export async function storeSlice(clientRecordingId: string, index: number, bytes: ArrayBuffer, durationMs: number): Promise<void> {
  await inTransaction(
    'readwrite',
    async ({ captures, slices }) => {
      const capture = await answerOf<StoredCapture | undefined>(captures.get(clientRecordingId))
      if (!capture) throw new Error('The capture of this slice is not in this browser.')
      if (capture.storedSlices !== index) throw new Error(`Slice ${index} came after ${capture.storedSlices} slices.`)
      const slice: StoredSlice = { clientRecordingId, index, bytes }
      await answerOf(slices.put(slice))
      await answerOf(
        captures.put({
          ...capture,
          storedSlices: index + 1,
          storedBytes: capture.storedBytes + bytes.byteLength,
          durationMs: Math.max(capture.durationMs, durationMs),
          touchedAt: Date.now(),
        } satisfies StoredCapture),
      )
    },
    'strict',
  )
  announceChange()
}

export async function readSlice(clientRecordingId: string, index: number): Promise<ArrayBuffer | null> {
  const slice = await inTransaction('readonly', ({ slices }) => answerOf<StoredSlice | undefined>(slices.get([clientRecordingId, index])))
  return slice?.bytes ?? null
}

/** The Server has this slice, so this browser lets it go. */
export async function confirmSlice(clientRecordingId: string, index: number): Promise<void> {
  await inTransaction('readwrite', async ({ captures, slices }) => {
    const capture = await answerOf<StoredCapture | undefined>(captures.get(clientRecordingId))
    if (!capture || capture.sentSlices !== index) return
    await answerOf(slices.delete([clientRecordingId, index]))
    await answerOf(captures.put({ ...capture, sentSlices: index + 1, problem: null } satisfies StoredCapture))
  })
  announceChange()
}

export const endCapture = (clientRecordingId: string) =>
  changeCapture(clientRecordingId, (capture) => (capture.isEnded ? capture : { ...capture, isEnded: true, touchedAt: Date.now() }))

export const updateCapture = (clientRecordingId: string, patch: Partial<Omit<StoredCapture, 'clientRecordingId' | 'owner' | 'storedSlices' | 'storedBytes' | 'sentSlices'>>) =>
  changeCapture(clientRecordingId, (capture) => ({ ...capture, ...patch }))

export async function removeCapture(clientRecordingId: string): Promise<void> {
  await inTransaction('readwrite', async ({ captures, slices }) => {
    await answerOf(slices.delete(IDBKeyRange.bound([clientRecordingId, 0], [clientRecordingId, Infinity])))
    await answerOf(captures.delete(clientRecordingId))
  })
  announceChange()
}
