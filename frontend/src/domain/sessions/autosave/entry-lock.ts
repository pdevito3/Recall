import { openChannel } from '@/lib/browser-channel'

import { isStateTakenFromKey, type OutboxEntry } from './outbox'
import { thisTab } from './tab-keys'

/**
 * One tab of a browser edits an entry at a time (ADR 0022). The tab that edits holds a Web Lock of the entry, and a
 * second tab shows the words with no edit until the person takes the entry there. A crash or a close of the tab frees
 * the lock. The tab that takes the entry starts from the words and the base that the last editor handed over, or that
 * it kept in storage, so no two tabs hold the same unsent words.
 */
export type EditingPlace = 'waiting' | 'here' | 'elsewhere'

/** The words of one saver, with the key that holds them. The key is null when the words are only in memory. */
export interface HandedEntry {
  storageKey: string | null
  entry: OutboxEntry
}

/** The words that the last editor gave over. The taker goes on with each key, and its next write replaces the key. */
export interface HandOver {
  from: string
  drafts: HandedEntry[]
}

/** A saver of one channel of the entry, in this tab. */
export interface LockParty {
  /** The words and the base of this saver, for the tab that takes the entry. Null when it holds nothing. This saver stops, and its key stays for the taker. */
  handOver(): HandedEntry | null
  /** This tab edits the entry now. `handed` holds the words that the last editor gave over, or it is null when none came in time. Gives the states of `handed` that this saver took. */
  takeOver(handed: HandOver | null): string[]
  /** The last editor gave over its words after this tab started from storage. Gives the states that this saver took. */
  takeLateHandOver(handed: HandOver): string[]
  /** Words of a draft that no saver of this tab writes, as words of another channel. Each goes on with its own base. Gives the states taken. */
  takeDraftsOfNoParty(drafts: HandedEntry[]): string[]
  noteEditingPlace(): void
  /** True while a page shows the words of this saver. */
  isShown(): boolean
}

type Message =
  | { kind: 'take'; lock: string; by: string }
  | ({ kind: 'handed'; lock: string; to: string | null } & HandOver)
  | { kind: 'want'; lock: string; by: string }
  | { kind: 'free'; lock: string; to: string }
  | { kind: 'took'; lock: string; states: string[] }

const channelName = 'recall.editors'
const markPrefix = 'recall.editor.'
/** A tab that lives answers a take at once. A frozen tab answers when it wakes, and its words then come late. */
export const handOverWaitMs = 500

let channel: BroadcastChannel | null | undefined
const locks = new Map<string, EntryLock>()

function hear({ data: message }: MessageEvent<Message>) {
  const lock = locks.get(message.lock)
  if (!lock) return
  if (message.kind === 'take') lock.hearTake(message.by)
  else if (message.kind === 'want') lock.hearWant(message.by)
  else if (message.kind === 'took') lock.hearTook(message.states)
  else if (message.kind === 'free') {
    if (message.to === thisTab) lock.hearFree()
  } else if (message.to === thisTab || message.to === null) lock.hearHandOver({ from: message.from, drafts: message.drafts })
}

function watchMarks() {
  window.addEventListener('pagehide', () => {
    for (const lock of locks.values()) lock.hide()
  })
  window.addEventListener('pageshow', (event) => {
    if (event.persisted) for (const lock of locks.values()) lock.show()
  })
  window.addEventListener('storage', ({ key, newValue }) => {
    if (!key?.startsWith(markPrefix)) return
    const lock = locks.get(key.slice(markPrefix.length))
    if (newValue === null) lock?.noteMarkGone()
    else if (newValue !== thisTab) lock?.isEditingHere()
  })
}

function listen() {
  if (channel !== undefined) return
  channel = openChannel(channelName)
  if (channel) channel.onmessage = hear
  watchMarks()
}

function talk(message: Message) {
  listen()
  channel?.postMessage(message)
}

const lockNameOf = (owner: string, clientSessionId: string) => `recall.edit.${encodeURIComponent(owner)}.${clientSessionId}`

/** Web Locks need a secure context. On plain HTTP on a LAN the browser has none, and the mark in storage alone names the tab that edits. */
// A page keeps its context, so its lock manager does not change.
const webLocks: LockManager | undefined = typeof navigator === 'undefined' ? undefined : navigator.locks
const hasWebLocks = () => webLocks !== undefined

const isAbort = (error: unknown) => error instanceof DOMException && error.name === 'AbortError'

/** The tab that edits an entry, in storage. A tab that wakes after a take reads it before it writes or sends. */
function writeMark(name: string) {
  try {
    localStorage.setItem(markPrefix + name, thisTab)
  } catch {
    // With no room, an older mark must not name an older editor. The tab that lost then hears the take or the loss of its lock.
    try {
      localStorage.removeItem(markPrefix + name)
    } catch {
      return
    }
  }
}

function removeMark(name: string) {
  try {
    if (localStorage.getItem(markPrefix + name) === thisTab) localStorage.removeItem(markPrefix + name)
  } catch {
    return
  }
}

function isMarkOfAnotherTab(name: string) {
  try {
    const mark = localStorage.getItem(markPrefix + name)
    return mark !== null && mark !== thisTab
  } catch {
    return false
  }
}

type Ask = 'ifFree' | 'inLine' | 'steal'

/** One request for the Web Lock. A steal by another tab ends it, also while this tab still waits for a hand-over. A release drops it, and a need takes back a drop that the lock manager did not answer yet. */
interface Request {
  isLost: boolean
  isDropped: boolean
  /** The lock manager can still answer: the request is not answered and not ended. */
  isPending: boolean
}

/** The wait of a grant for the words of the tab that held the lock. Each gives the first hand-over that came, or null. */
interface HandOverCatch {
  ask(): Promise<HandOver | null>
  end(): HandOver | null
}

export class EntryLock {
  readonly name: string
  private place: EditingPlace = 'waiting'
  private readonly parties = new Set<LockParty>()
  private unlock: (() => void) | null = null
  private waitInLine: AbortController | null = null
  private handOverWaiter: ((handed: HandOver) => void) | null = null
  /** The words that this tab gave when it lost the lock, until a tab says that it took them. They go again to each tab that takes, and they come back with the lock. */
  private lastHanded: HandedEntry[] | null = null
  private isClosed = false
  /** The newest request of this tab. Null, or dropped, while this tab does not need the entry. */
  private request: Request | null = null
  /** True from Edit here until the hand-over came or the wait ended. */
  private isTaking = false

  constructor(name: string, isNewEntry: boolean) {
    this.name = name
    listen()
    // No other tab knows the ID of a new entry, so this tab edits it at once.
    if (isNewEntry) {
      this.place = 'here'
      writeMark(name)
    }
  }

  get editingPlace() {
    return this.place
  }

  /** A saver that joins asks for the lock when this tab does not ask yet. A saver that joins while this tab edits takes the words that other tabs left in storage. */
  join(party: LockParty) {
    const isNeeded = this.isNeeded()
    this.parties.add(party)
    if (!isNeeded) this.need()
    else if (this.place === 'here') party.takeOver(null)
  }

  private isNeeded() {
    return this.request !== null && !this.request.isDropped
  }

  /** A page or words of this tab need the entry again after a release. A drop that the lock manager did not answer yet is undone, so the page does not wait behind its own request. */
  need() {
    if (this.isClosed || this.isNeeded()) return
    if (this.request?.isPending) this.request.isDropped = false
    else this.ask('ifFree')
  }

  leave(party: LockParty) {
    this.parties.delete(party)
  }

  /** True while this tab edits, and false from the moment this tab knows that another tab took the entry. */
  isEditingHere() {
    if (this.place !== 'here') return false
    if (isMarkOfAnotherTab(this.name)) this.lose()
    return this.place === 'here'
  }

  /** Edit here: the lock moves to this tab, and the other tab stops at once. */
  takeHere() {
    // A second press while the first waits for the hand-over would end the wait of the first.
    if (this.place === 'here' || this.isClosed || this.isTaking) return
    this.isTaking = true
    this.waitInLine?.abort()
    this.waitInLine = null
    this.ask('steal')
  }

  /**
   * A page that shows the entry with no edit asks the tab that edits to give the entry up when no page of that tab
   * shows it, as when a load of that tab took the words that a closed or reloaded tab left.
   */
  askForTheEntry() {
    if (this.place !== 'elsewhere' || this.isClosed || !this.isShown()) return
    talk({ kind: 'want', lock: this.name, by: thisTab })
  }

  hearWant(by: string) {
    if (by === thisTab || this.place !== 'here' || this.isShown()) return
    talk({ kind: 'free', lock: this.name, to: by })
  }

  /** The tab that edits has no page of the entry, so this page takes it, with the hand-over of a take. */
  hearFree() {
    if (this.place === 'elsewhere' && this.isShown()) this.takeHere()
  }

  private isShown() {
    return [...this.parties].some((party) => party.isShown())
  }

  /**
   * This tab holds nothing of the entry, and no page shows it, so the lock goes free. A saver that waits in line and
   * holds nothing must not get the lock later, when no page or notice knows it. This lock and its savers stay, so a page
   * that shows the entry again asks with them, and no second lock of the entry asks in this tab.
   */
  release() {
    const request = this.request
    if (this.isClosed || !request || request.isDropped) return
    request.isDropped = true
    // A dropped steal gets no grant, so Edit here must work again.
    this.isTaking = false
    if (this.waitInLine) {
      // An aborted wait gets no answer.
      request.isPending = false
      this.waitInLine.abort()
      this.waitInLine = null
    }
    if (this.place === 'here') removeMark(this.name)
    this.unlock?.()
    this.unlock = null
    if (this.place === 'waiting') return
    this.place = 'waiting'
    this.notify()
  }

  close() {
    if (this.isClosed) return
    this.isClosed = true
    this.waitInLine?.abort()
    this.waitInLine = null
    if (this.place === 'here') removeMark(this.name)
    this.unlock?.()
    this.unlock = null
    locks.delete(this.name)
  }

  hearTake(by: string) {
    if (by === thisTab) return
    // With Web Locks, only a steal ends the lock that this tab holds, and that end gives the words. A take of a tab that holds no lock, as one that woke with an old grant, changes nothing.
    if (this.place === 'here' && this.unlock !== null && hasWebLocks()) return
    const handed = this.place === 'here' ? null : this.untakenHanded()
    if (handed !== null) {
      // The words stay here until the taker says that it took them.
      talk({ kind: 'handed', lock: this.name, to: by, from: thisTab, drafts: handed })
      return
    }
    this.lose(by)
  }

  hearHandOver(handed: HandOver) {
    if (this.handOverWaiter) {
      this.handOverWaiter(handed)
      return
    }
    if (this.place !== 'here' || handed.drafts.length === 0) return
    this.giveOut(handed, (party) => party.takeLateHandOver(handed))
  }

  /** A page in the back-forward cache edits nothing. With no Web Locks, its mark goes, so another tab can edit. */
  hide() {
    if (this.place === 'here' && !hasWebLocks()) removeMark(this.name)
  }

  show() {
    if (this.place !== 'here' || hasWebLocks()) return
    if (isMarkOfAnotherTab(this.name)) this.lose()
    else writeMark(this.name)
  }

  /** The tab that edited left. With no Web Locks, a tab that waits takes the entry now. With Web Locks, the lock comes to it in line. */
  noteMarkGone() {
    if (this.place === 'elsewhere' && !hasWebLocks()) this.ask('ifFree')
  }

  private ask(how: Ask) {
    const request: Request = { isLost: false, isDropped: false, isPending: webLocks !== undefined }
    this.request = request
    if (!webLocks) {
      this.askWithNoLocks(how, request)
      return
    }
    const waitInLine = how === 'inLine' ? new AbortController() : null
    if (waitInLine) this.waitInLine = waitInLine
    const options: LockOptions = how === 'steal' ? { steal: true } : how === 'ifFree' ? { ifAvailable: true } : { signal: waitInLine?.signal }
    let isHeld = false
    webLocks
      .request(this.name, options, async (lock) => {
        request.isPending = false
        if (this.isClosed || request.isDropped) return
        if (lock === null) {
          this.becomeReader()
          return
        }
        isHeld = true
        if (this.waitInLine === waitInLine) this.waitInLine = null
        // The tab that held the lock gives its words when it loses the lock, and they can come before the answer below.
        const handOver = this.catchHandOver()
        // A tab that wakes from a freeze runs a grant that another tab can have stolen since. The lock manager answers in order, so a steal before this answer ends the request first.
        await webLocks.query()
        if (this.isClosed || request.isDropped || request.isLost) {
          // Words that came for this grant go on to the tab that holds the lock, and this tab keeps them until a tab says that it took them.
          const caught = handOver.end()
          if (caught !== null && caught.drafts.length > 0) this.forward({ from: caught.from, drafts: [...(this.untakenHanded() ?? []), ...caught.drafts] })
          if (request.isLost) this.waitAgain(how, request)
          return
        }
        const held = new Promise<void>((resolve) => {
          this.unlock = resolve
        })
        await this.grant(request, how === 'steal', handOver)
        await held
      })
      .catch((error: unknown) => {
        request.isPending = false
        if (!isHeld && how === 'steal') this.isTaking = false
        if (!isHeld || !isAbort(error)) return
        request.isLost = true
        this.lose()
      })
  }

  /** With no Web Locks, as on plain HTTP, the mark in storage names the tab that edits. A tab that crashed leaves its mark, and Edit here takes the entry. */
  private askWithNoLocks(how: Ask, request: Request) {
    if (how === 'inLine') return
    if (how === 'ifFree' && this.place !== 'here' && isMarkOfAnotherTab(this.name)) {
      this.becomeReader()
      return
    }
    this.unlock = () => undefined
    void this.grant(request, how === 'steal', this.catchHandOver())
  }

  /** The lock manager took the lock from this tab before this tab acted on the grant, so this tab holds no words and waits as before. */
  private waitAgain(how: Ask, request: Request) {
    if (this.request !== request) return
    if (how === 'steal') this.isTaking = false
    if (this.place === 'waiting') this.becomeReader()
    else this.ask('inLine')
  }

  private becomeReader() {
    this.place = 'elsewhere'
    this.notify()
    this.ask('inLine')
    this.askForTheEntry()
  }

  private async grant(request: Request, isTake: boolean, handOver: HandOverCatch) {
    writeMark(this.name)
    // Words that this tab gave when it lost the lock, and that no tab took, come back with the lock.
    const ownHanded = this.untakenHanded()
    this.lastHanded = null
    const wasHere = this.place === 'here'
    const fromOthers = isTake ? await handOver.ask() : handOver.end()
    if (isTake) this.isTaking = false
    if (this.isClosed || request.isDropped) return
    const handed = ownHanded === null ? fromOthers : { from: fromOthers?.from ?? thisTab, drafts: [...ownHanded, ...(fromOthers?.drafts ?? [])] }
    if (request.isLost) {
      // A third tab took the entry during the wait. The words that came go on to it, and this tab waits in line again.
      if (handed && handed.drafts.length > 0) this.forward(handed)
      this.ask('inLine')
      return
    }
    this.place = 'here'
    if (!wasHere) this.giveOut(handed, (party) => party.takeOver(handed))
    this.notify()
  }

  /**
   * Each saver takes the words of its draft, and the first saver takes the words of a draft that no saver here writes,
   * as words of another channel. The tab that gave the words keeps each state until it hears that a tab took it.
   */
  private giveOut(handed: HandOver | null, take: (party: LockParty) => string[]) {
    const parties = [...this.parties]
    const taken = new Set(parties.flatMap(take))
    const left = (handed?.drafts ?? []).filter(({ entry }) => entry.stateId !== undefined && !taken.has(entry.stateId))
    const [first] = parties
    if (first && left.length > 0) for (const state of first.takeDraftsOfNoParty(left)) taken.add(state)
    if (taken.size > 0) talk({ kind: 'took', lock: this.name, states: [...taken] })
  }

  /** The handed words that no tab took. A tab that took a key from storage, and not from a hand-over, says nothing, so the key tells. */
  private untakenHanded() {
    const left = (this.lastHanded ?? []).filter(({ storageKey, entry }) => storageKey === null || entry.stateId === undefined || !isStateTakenFromKey(storageKey, entry.stateId))
    this.lastHanded = left.length > 0 ? left : null
    return this.lastHanded
  }

  hearTook(states: string[]) {
    if (this.lastHanded === null) return
    const left = this.lastHanded.filter(({ entry }) => !states.includes(entry.stateId ?? ''))
    this.lastHanded = left.length > 0 ? left : null
  }

  private forward(handed: HandOver) {
    talk({ kind: 'handed', lock: this.name, to: null, ...handed })
    this.lastHanded = handed.drafts
  }

  /** From now on, the first hand-over to this tab ends the wait. `ask` asks the other tabs for their words and waits for them a short time, and `end` waits no more. */
  private catchHandOver(): HandOverCatch {
    let limit: ReturnType<typeof setTimeout> | undefined
    let caught: HandOver | null | undefined
    let finish: (handed: HandOver | null) => void = () => undefined
    const handed = new Promise<HandOver | null>((resolve) => {
      finish = (one) => {
        clearTimeout(limit)
        if (this.handOverWaiter === finish) this.handOverWaiter = null
        if (caught === undefined) caught = one
        resolve(one)
      }
    })
    this.handOverWaiter = finish
    return {
      ask: () => {
        if (this.handOverWaiter === finish) limit = setTimeout(() => finish(null), handOverWaitMs)
        talk({ kind: 'take', lock: this.name, by: thisTab })
        return handed
      },
      end: () => {
        finish(null)
        return caught ?? null
      },
    }
  }

  /** Another tab took the entry. Each saver stops, and its words go to that tab. A lock that this tab still holds goes free, so a wrong mark cannot keep it. */
  private lose(to: string | null = null) {
    if (this.place !== 'here') return
    this.place = 'elsewhere'
    this.unlock?.()
    this.unlock = null
    const drafts = [...this.parties].flatMap((party) => party.handOver() ?? [])
    talk({ kind: 'handed', lock: this.name, to, from: thisTab, drafts })
    this.lastHanded = drafts.length > 0 ? drafts : null
    this.notify()
    if (!this.isClosed) this.ask('inLine')
  }

  private notify() {
    for (const party of this.parties) party.noteEditingPlace()
  }
}

/** The lock of an entry in this tab. `isNewEntry` is true for an entry that this tab just started. */
export function lockOfEntry(owner: string, clientSessionId: string, isNewEntry = false): EntryLock {
  const name = lockNameOf(owner, clientSessionId)
  const existing = locks.get(name)
  if (existing) return existing
  const lock = new EntryLock(name, isNewEntry)
  locks.set(name, lock)
  return lock
}

/** A tab that leaves ends each lock and mark, so the next tab of this browser edits at once. */
export function closeEntryLocks() {
  for (const lock of [...locks.values()]) lock.close()
}
