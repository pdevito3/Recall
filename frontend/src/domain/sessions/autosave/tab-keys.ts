import { newUuid } from '@/lib/new-uuid'

/**
 * Ownership of unsent text in localStorage. Each page load writes its text under keys that name the page load, and a key
 * never changes: a newer text gets a new key. So a remove can take only the text that the remover read, and it does not
 * depend on an atomic read and remove, which localStorage does not have across tabs. Another tab takes a key only after
 * its writer left or does not answer, and a wrong guess sends the same text twice and cannot lose it. These APIs work on
 * plain HTTP: localStorage and BroadcastChannel need no secure context, and Web Locks do.
 */
export const thisTab = newUuid()

const channelName = 'recall.tabs'
const leftMarkPrefix = 'recall.left.'
const answerWaitMs = 500

export interface KeyOfATab {
  storageKey: string
  /** Null when the key has no writer in its name. No live tab writes such a key, so any tab can take it. */
  writer: string | null
  stored: string | null
}

type Message =
  | { kind: 'who'; nonce: string }
  | { kind: 'here'; nonce: string; writer: string }
  | { kind: 'left'; writer: string }
  | { kind: 'settle'; nonce: string }
  | { kind: 'settling'; nonce: string; writer: string }
  | { kind: 'settled'; nonce: string; writer: string; report: OtherTabReport }

export interface OtherTabReport {
  wordsOnlyInMemory: string[]
  hasUnsavedTags: boolean
}

let sequence = 0
const keysOfThisTab = new Set<string>()
const waiters = new Map<string, (message: Message) => void>()
const otherTabWatchers = new Set<() => void>()
let answerASettle: (() => Promise<OtherTabReport>) | null = null
let channel: BroadcastChannel | null | undefined

function hear({ data: message }: MessageEvent<Message>) {
  switch (message.kind) {
    case 'who':
      talk({ kind: 'here', nonce: message.nonce, writer: thisTab })
      break
    case 'left':
      for (const watcher of otherTabWatchers) watcher()
      break
    case 'settle':
      if (!answerASettle) break
      talk({ kind: 'settling', nonce: message.nonce, writer: thisTab })
      void answerASettle().then((report) => talk({ kind: 'settled', nonce: message.nonce, writer: thisTab, report }))
      break
    case 'here':
    case 'settling':
    case 'settled':
      waiters.get(message.nonce)?.(message)
      break
  }
}

/** With no channel in this browser no tab answers, so a tab takes the keys of each other tab. The same text then goes out twice, and it cannot be lost. */
function channelToOtherTabs() {
  if (channel !== undefined) return channel
  try {
    channel = Object.assign(new BroadcastChannel(channelName), { onmessage: hear })
  } catch {
    channel = null
  }
  return channel
}

function talk(message: Message) {
  channelToOtherTabs()?.postMessage(message)
}

/** The channel must be open before another tab asks, also when this tab has written nothing. */
export function joinTheOtherTabs(settle: () => Promise<OtherTabReport>) {
  answerASettle = settle
  channelToOtherTabs()
}

export const hasWriterLeft = (writer: string | null) => {
  try {
    return writer === null || localStorage.getItem(leftMarkPrefix + writer) !== null
  } catch {
    return false
  }
}

/** Writes the text under a new key, and only then removes the older key of this tab. With no room for both, the older key goes first, and comes back if the new text still has no room. */
export function writeKeyOfThisTab(prefix: string, previousKey: string | null, value: string): string | null {
  const storageKey = `${prefix}${thisTab}.${(sequence += 1)}`
  try {
    localStorage.setItem(storageKey, value)
  } catch {
    if (!replaceWithNoRoom(storageKey, previousKey, value)) return null
    keysOfThisTab.add(storageKey)
    if (previousKey !== null) keysOfThisTab.delete(previousKey)
    return storageKey
  }
  keysOfThisTab.add(storageKey)
  if (previousKey !== null) removeKey(previousKey)
  return storageKey
}

function replaceWithNoRoom(storageKey: string, previousKey: string | null, value: string) {
  if (previousKey === null) return false
  const previous = localStorage.getItem(previousKey)
  localStorage.removeItem(previousKey)
  try {
    localStorage.setItem(storageKey, value)
    return true
  } catch {
    if (previous !== null) restoreKey(previousKey, previous)
    return false
  }
}

/** Another page can have filled the room meanwhile. Then the words are only in this page, and the caller tells the person. */
function restoreKey(storageKey: string, value: string) {
  try {
    localStorage.setItem(storageKey, value)
  } catch {
    keysOfThisTab.delete(storageKey)
  }
}

export function removeKey(storageKey: string) {
  keysOfThisTab.delete(storageKey)
  try {
    localStorage.removeItem(storageKey)
  } catch {
    return
  }
}

const writerIn = (rest: string) => {
  const [writer, sequenceText, ...more] = rest.split('.')
  return more.length === 0 && /^\d+$/.test(sequenceText ?? '') ? (writer ?? null) : null
}

/** A failed read says nothing about the key, so it counts as stored. */
export function isKeyStored(storageKey: string) {
  try {
    return localStorage.getItem(storageKey) !== null
  } catch {
    return true
  }
}

export function keysUnder(prefix: string): KeyOfATab[] {
  try {
    return Object.keys(localStorage)
      .filter((storageKey) => storageKey.startsWith(prefix))
      .map((storageKey) => ({ storageKey, writer: writerIn(storageKey.slice(prefix.length)), stored: localStorage.getItem(storageKey) }))
  } catch {
    return []
  }
}

/** Each tab that lives answers, also when it runs behind. A frozen tab does not answer, and then its text goes out twice. */
function writersThatAnswer(): Promise<Set<string>> {
  const nonce = newUuid()
  const writers = new Set<string>()
  waiters.set(nonce, (message) => {
    if (message.kind === 'here') writers.add(message.writer)
  })
  talk({ kind: 'who', nonce })
  return new Promise((resolve) =>
    setTimeout(() => {
      waiters.delete(nonce)
      resolve(writers)
    }, answerWaitMs),
  )
}

/** A key of a tab that said that it left goes at once. A tab that said nothing gets the time to answer, and only a tab with no answer loses its keys. */
export async function takeKeysOfTabsThatLeft(prefix: string, take: (keys: KeyOfATab[]) => void) {
  const ofOtherTabs = () => keysUnder(prefix).filter(({ writer }) => writer !== thisTab)
  take(ofOtherTabs().filter(({ writer }) => hasWriterLeft(writer)))
  if (ofOtherTabs().length === 0) return
  const live = await writersThatAnswer()
  take(ofOtherTabs().filter(({ writer }) => writer === null || !live.has(writer)))
}

/** Each other tab tries to send its text. The answer names the words that only that tab holds, which a sign-out would lose. */
export function askOtherTabsToSettle(limitMs: number): Promise<OtherTabReport[]> {
  const nonce = newUuid()
  const waitingFor = new Set<string>()
  const reports: OtherTabReport[] = []
  // A tab that answers fast must not end the wait before a slower tab said that it heard.
  let hasEachTabHeard = false
  return new Promise((resolve) => {
    const finish = () => {
      waiters.delete(nonce)
      clearTimeout(limit)
      resolve(reports)
    }
    const limit = setTimeout(finish, limitMs)
    waiters.set(nonce, (message) => {
      if (message.kind === 'settling') waitingFor.add(message.writer)
      if (message.kind !== 'settled') return
      waitingFor.delete(message.writer)
      reports.push(message.report)
      if (hasEachTabHeard && waitingFor.size === 0) finish()
    })
    talk({ kind: 'settle', nonce })
    setTimeout(() => {
      hasEachTabHeard = true
      if (waitingFor.size === 0) finish()
    }, answerWaitMs)
  })
}

export function noteThisTabLeaves() {
  if (keysOfThisTab.size === 0) return
  try {
    localStorage.setItem(leftMarkPrefix + thisTab, '1')
  } catch {
    // With no room for the mark, another tab asks, gets no answer, and takes the keys a moment later.
  }
  talk({ kind: 'left', writer: thisTab })
}

/** A page back from the back-forward cache lives again. If another tab took a key meanwhile, this page does not send that text. */
export function noteThisTabIsBack() {
  try {
    localStorage.removeItem(leftMarkPrefix + thisTab)
  } catch {
    return
  }
}

/** A mark with no key left is rubbish, and it names no words. */
export function removeLeftMarksWithNoKey() {
  try {
    const keys = Object.keys(localStorage)
    for (const mark of keys.filter((key) => key.startsWith(leftMarkPrefix))) {
      const writer = mark.slice(leftMarkPrefix.length)
      if (!keys.some((key) => key.startsWith('recall.unsent') && key.includes(`.${writer}.`))) localStorage.removeItem(mark)
    }
  } catch {
    return
  }
}

export function watchOtherTabs(watcher: () => void) {
  otherTabWatchers.add(watcher)
  return () => {
    otherTabWatchers.delete(watcher)
  }
}
