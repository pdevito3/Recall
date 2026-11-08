import { notifyManager, type QueryClient } from '@tanstack/react-query'

import type { DraftSaveResultDto } from '@/api/generated/models/types/draft-save-result-dto'
import type { SessionDto } from '@/api/generated/models/types/session-dto'
import { addSession } from '@/api/generated/sessions/clients/add-session'
import { getSession } from '@/api/generated/sessions/clients/get-session'
import { saveCleanedDraft } from '@/api/generated/sessions/clients/save-cleaned-draft'
import { saveRawDraft } from '@/api/generated/sessions/clients/save-raw-draft'
import { Notification } from '@/components/notification'
import { newUuid } from '@/lib/new-uuid'
import { retryUnlessRefused } from '@/lib/problem-details'

import { SessionKeys, type DraftChannel } from '../apis/session.keys'
import { patchTimelineRow, refreshTimelineHead } from '../apis/timeline-cache'
import { holdsEachWordOf, joinWords, mentionedPersonIds, mergeWords, plainPreviewOf } from '../editor/content'
import { mergeDraft } from '../editor/merge-draft'
import { pointAtCreatedSession, pointAtNewEntry } from './composer-pointer'
import { closeEntryLocks, lockOfEntry, type EditingPlace, type EntryLock, type HandedEntry, type HandOver, type LockParty } from './entry-lock'
import { readOutbox, removeFromOutbox, writeToOutbox, type OutboxEntry } from './outbox'
import { emptyQueue, queueWithRefusal, queueWithUnanswered, type SaveQueue, type SaveStatus, type Stamped } from './save-queue'
import { isARowThatIsGone, isFinalRefusal, SerialSaver } from './serial-saver'
import { isKeyStored, thisTab, type OtherTabReport } from './tab-keys'

export type DraftConflict = 'wordsMovedToHistory' | 'discardedInAnotherTab' | null

export type ShownSaveStatus = SaveStatus | { kind: 'savedInHistory'; at: string } | { kind: 'waitingForAnotherWriter' }

export interface DraftSnapshot {
  status: SaveStatus
  conflict: DraftConflict
  sessionId: string | null
  baseRevision: number
  movedFromDeletedEntry: boolean
  isOnlyInMemory: boolean
  waitsForAnotherWriter: boolean
  /** Where the entry is edited. A page edits only `here`, and it shows the words with no edit otherwise. */
  editing: EditingPlace
  /** The newest unsent words of the tab that edits, from its key. Null when that tab holds none. */
  wordsOfTheEditor: string | null
}

export type DraftNotice =
  | { kind: 'moved'; saver: DraftSaver; sessionId: string | null }
  | { kind: 'joined'; saver: DraftSaver; sessionId: string | null }
  | { kind: 'inHistory'; saver: DraftSaver; sessionId: string | null; words: string }
  | { kind: 'refused'; saver: DraftSaver; reason: string; words: string }
  | { kind: 'waiting'; saver: DraftSaver; words: string }
  | { kind: 'onlyInMemory'; saver: DraftSaver; reason: string | null; canDiscard: boolean; words: string }

export function shownStatusOf({ status, conflict, waitsForAnotherWriter }: DraftSnapshot): ShownSaveStatus {
  if (waitsForAnotherWriter && status.kind !== 'refused') return { kind: 'waitingForAnotherWriter' }
  if (status.kind !== 'saved' || conflict === null) return status
  return conflict === 'wordsMovedToHistory' ? { kind: 'savedInHistory', at: status.at } : { kind: 'idle' }
}

export type WhyNewWords = 'heldWordsJoined' | 'draftHoldsMore' | 'mergedIntoNewest'

const lossesBeforeAWait = 3
const firstWaitMs = 1_000
const longestWaitMs = 30_000

/** Step 6 of the steps after `Lost` in `docs/ios-api-changes.md`, as the iOS client does it: a writer slower than another writer loses each save, so after 3 `Lost` in a row the next read waits, 1 s first, twice as long after each later `Lost` up to 30 s, plus a random part of up to half the wait. `jitter` is in 0 to 1. */
export function waitAfterLosses(lostInARow: number, jitter: number): number {
  const doublings = lostInARow - lossesBeforeAWait
  if (doublings < 0) return 0
  const wait = Math.min(firstWaitMs * 2 ** Math.min(doublings, 8), longestWaitMs)
  return wait + (wait / 2) * Math.min(Math.max(jitter, 0), 1)
}

/** A refused read is no refusal of the words, so the save goes again. A read of an entry that is gone moves the words. */
const asReadFailure = (error: unknown): never => {
  throw isFinalRefusal(error) && !isARowThatIsGone(error) ? new Error('The read failed.') : error
}

const readRetryMs = 1_000

/** A read with one retry, as the query of a Session has. */
async function readSession(id: string, signal?: AbortSignal): Promise<SessionDto> {
  const read = () => getSession({ path: { id }, signal, options: { suppressErrorToast: true } })
  try {
    return await read()
  } catch (error) {
    if (signal?.aborted || !(error instanceof Error) || !retryUnlessRefused(0, error)) throw error
    await new Promise((resolve) => setTimeout(resolve, readRetryMs))
    return read()
  }
}

/** A save after a loss needs a new `savedAt`. The same `savedAt` with the same words is a replay, and it gets `Lost` again. */
const stampAfter = (...stamps: string[]) => new Date(Math.max(Date.now(), ...stamps.map((savedAt) => Date.parse(savedAt) + 1).filter(Number.isFinite))).toISOString()

/** Where the words of a save are, by the text of the Draft. A word of these words that is not in the Draft needs the person. */
export type WordsFate = 'areTheDraft' | 'areInTheDraft' | 'onlyInHistory'

export function fateOfWords(words: string, draft: string): WordsFate {
  if (words === draft) return 'areTheDraft'
  return holdsEachWordOf(draft, words) ? 'areInTheDraft' : 'onlyInHistory'
}

/** `reply` is null when no save went: a sign-out in another tab discarded the key, or the send was the read after `Lost`. `newest` is the read that a save needs after `Kept` or `Lost`. */
interface SaveAnswer {
  reply: DraftSaveResultDto | null
  newest: SessionDto | null
  isReadAfterLoss?: boolean
}

export interface DraftTarget {
  owner: string
  channel: DraftChannel
  clientSessionId: string
  startedAt: string
  sessionId: string | null
  baseRevision: number
  /** The text of the Draft at `baseRevision`, so that a merge takes only the change of the person. */
  baseText: string
  /** Asked at the create, because the person can turn capture off after the first word. */
  location?: () => Promise<{ latitude: number; longitude: number } | null>
}

/** The queue of a key: the change in flight goes again first with its own `savedAt`, so a save that won answers `Kept`, and then the newest change goes. */
function queueOf({ change, sent, refusal }: OutboxEntry): SaveQueue<string> {
  if (change && refusal) return queueWithRefusal(change, refusal)
  if (change) return sent ? queueWithUnanswered(sent, change) : queueWithUnanswered(change)
  return emptyQueue()
}

/** What a tab that takes a key sends: the words, their base, and the entry. A change of `sent` alone sends no other words. */
const wordsOfAState = ({ sessionId, baseRevision, baseText, change, held, wordsInHistory, refusal, readsFirst }: OutboxEntry) =>
  JSON.stringify([sessionId, baseRevision, baseText, change?.value ?? null, held ?? null, wordsInHistory ?? null, refusal ?? null, readsFirst ?? false])

const pauseInTypingMs = 800
const holdReadLimitMs = 30_000
const holdReadRetryMs = 2_000
const takeReadLimitMs = 3_000
const previewLength = 200

export class DraftSaver implements LockParty {
  private target: DraftTarget
  private readonly queryClient: QueryClient
  private readonly saver: SerialSaver<string, SaveAnswer>
  private snapshot: DraftSnapshot
  private readonly listeners = new Set<() => void>()
  private replyCount = 0
  private wordsInHistory: string | null = null
  private wordsOnServer: string | null = null
  private heldWords: string | null = null
  private ownKey: string | null = null
  /** The last state that a write of this saver put in a key. */
  private kept: { storageKey: string; stateId: string; words: string } | null = null
  private hasUnseenJoin = false
  private reportsArrival = false
  private holdRun = 0
  private readonly newWordsListeners = new Set<(words: string, why: WhyNewWords) => void>()
  private isStopped = false
  private isDiscarded = false
  private isBeingDeleted = false
  private baseText: string
  private lostInARow = 0
  private readsBeforeNextSave = false
  private lock: EntryLock
  /** The state of this saver changes with each write of its key, and a hand-over names the state that it holds. */
  private stateId = nextStateId()
  /** The states of other tabs that this saver took, so a later hand-over of the same state goes once. */
  private readonly takenStates = new Set<string>()
  /** A saver that finishes the words of another tab that left, beside the saver of this tab. It edits no page. */
  private readonly isHelper: boolean

  constructor(target: DraftTarget, queryClient: QueryClient, options: { taken?: HandedEntry; isNewEntry?: boolean } = {}) {
    this.target = target
    this.queryClient = queryClient
    this.baseText = target.baseText
    this.isHelper = options.taken !== undefined
    this.snapshot = {
      status: { kind: 'idle' },
      conflict: null,
      sessionId: target.sessionId,
      baseRevision: target.baseRevision,
      movedFromDeletedEntry: false,
      isOnlyInMemory: false,
      waitsForAnotherWriter: false,
      editing: 'waiting',
      wordsOfTheEditor: null,
    }
    this.saver = new SerialSaver({
      debounceMs: pauseInTypingMs,
      send: (change, signal) => this.send(change, signal),
      onReply: (reply, change) => this.handleReply(reply, change),
      onQueueChange: () => this.keep(),
    })
    this.saver.subscribe(() => this.update({ status: this.saver.getStatus() }))
    this.lock = lockOfEntry(target.owner, target.clientSessionId, options.isNewEntry)
    this.lock.join(this)
    if (this.lock.editingPlace === 'here') this.update({ editing: 'here' })
    else this.noteEditingPlace()
    if (options.taken) this.adopt(options.taken.entry, options.taken.storageKey)
  }

  edit(text: string) {
    if (!this.isEditing()) return
    this.forgetAKeyThatIsGone()
    this.saver.edit(text)
  }

  flush() {
    this.saver.flush()
  }

  async settle() {
    const isSent = await this.saver.settle()
    return isSent && this.heldWords === null
  }

  stop() {
    this.isStopped = true
    this.saver.dispose()
  }

  noteDelete(isRunning: boolean) {
    this.isBeingDeleted = isRunning
  }

  wordsInHold() {
    return this.heldWords
  }

  /** Words typed before a read answered are words of this tab, also when another tab edits the entry. They join when this tab edits. */
  holdInOutbox(text: string) {
    this.forgetAKeyThatIsGone()
    this.heldWords = text
    this.keep()
  }

  releaseFromOutbox() {
    this.heldWords = null
    this.keep()
  }

  /** A page that reads the entry joins the held words itself, so the read of the saver stops. */
  takeHold() {
    this.holdRun += 1
  }

  /** Words typed before a read answered go after the words of the entry, and a block that the entry already holds does not go twice. One write moves them from the hold to the queue. */
  joinHoldInto(words: string) {
    if (this.heldWords === null || !this.isEditing()) return words
    const joined = mergeWords(words, this.heldWords)
    this.heldWords = null
    this.forgetAKeyThatIsGone()
    this.saver.edit(joined)
    return joined
  }

  /** A page that shows this entry opens these words at once, and its next key must not save the old text over them. */
  subscribeToNewWords(listener: (words: string, why: WhyNewWords) => void) {
    this.newWordsListeners.add(listener)
    return () => {
      this.newWordsListeners.delete(listener)
    }
  }

  private showNewWords(words: string, why: WhyNewWords) {
    for (const listener of this.newWordsListeners) listener(words, why)
  }

  /** Held words were typed before a read answered. They join the words of the entry and must not replace them, also with no page open. */
  async joinHoldWithNoPage() {
    const run = ++this.holdRun
    const isCurrent = () => run === this.holdRun && !this.isStopped && this.isEditing()
    const id = this.snapshot.sessionId
    while (isCurrent() && this.heldWords !== null) {
      const stopRead = new AbortController()
      const limit = setTimeout(() => stopRead.abort(), holdReadLimitMs)
      const session = id === null
        ? null
        : await this.readSessionNow(id, stopRead.signal).catch((error: unknown) => (isARowThatIsGone(error) ? null : undefined))
      clearTimeout(limit)
      if (!isCurrent()) return
      if (session === undefined) {
        await new Promise((resolve) => setTimeout(resolve, holdReadRetryMs))
        continue
      }
      if (this.heldWords === null) return
      const unsettled = this.unsettledText()
      if (session && unsettled === null) this.moveBaseTo(session.rawDraftRevision, session.rawText)
      const joined = this.joinHoldInto(unsettled ?? session?.rawText ?? '')
      this.showNewWords(joined, 'heldWordsJoined')
      if (this.newWordsListeners.size === 0) this.reportsArrival = true
      refreshDraftNotices()
    }
  }

  dismissMovedNotice() {
    this.update({ movedFromDeletedEntry: false })
    this.keep()
  }

  dismissJoinNotice() {
    this.hasUnseenJoin = false
    refreshDraftNotices()
  }

  /** All the words that only this saver holds. */
  unsentWords() {
    const unsent = [this.unsettledText(), this.heldWords].filter((text) => text !== null)
    return unsent.length > 0 ? unsent.reduce(joinWords) : null
  }

  notice(): DraftNotice | null {
    const { status, movedFromDeletedEntry, sessionId, isOnlyInMemory, conflict } = this.snapshot
    const words = this.unsentWords() ?? ''
    const hasNoPage = this.listeners.size === 0
    // An open page keeps its saver and its editor, so a discard there would leave a page that saves nothing.
    if (isOnlyInMemory && words !== '') return { kind: 'onlyInMemory', saver: this, reason: status.kind === 'refused' ? status.message : null, canDiscard: hasNoPage, words }
    if (status.kind === 'refused' && hasNoPage) return { kind: 'refused', saver: this, reason: status.message, words }
    if (status.kind === 'waiting' && hasNoPage) return { kind: 'waiting', saver: this, words }
    if (movedFromDeletedEntry) return { kind: 'moved', saver: this, sessionId }
    if (hasNoPage && conflict === 'wordsMovedToHistory') return { kind: 'inHistory', saver: this, sessionId, words: this.wordsInHistory ?? '' }
    return hasNoPage && this.hasUnseenJoin ? { kind: 'joined', saver: this, sessionId } : null
  }

  unsettledText() {
    return this.saver.newestUnsettledValue()
  }

  /** A hold can have joined the entry after Done, so these words can be newer than the words at Done. */
  newestWords() {
    const { conflict } = this.snapshot
    return this.unsettledText() ?? (conflict === null ? this.wordsOnServer : conflict === 'wordsMovedToHistory' ? this.wordsInHistory : null)
  }

  /** The words a page must open with: those not yet settled, or those that the Draft does not hold. */
  wordsToOpen() {
    const kept = this.unsettledText() ?? (this.snapshot.conflict === 'wordsMovedToHistory' ? this.wordsInHistory : null)
    return kept === null ? null : this.joinHoldInto(kept)
  }

  isHoldingWordsOnlyInMemory() {
    return this.snapshot.isOnlyInMemory && !this.isStopped
  }

  lastSavedWords() {
    return this.wordsOnServer
  }

  hasConflict() {
    return this.snapshot.conflict !== null
  }

  dismissConflict() {
    this.wordsInHistory = null
    this.update({ conflict: null })
    this.keep()
  }

  /**
   * A notice that stays after a merge: words that left the Draft after a save took them, or words that a sign-out in another tab discarded.
   * The person asks for the words of the page in the newest Draft. Only the change of the person since the base goes in, so no paragraph shows twice.
   * The answer is the words to show, and null means that nothing changed. A page that opened before the notice came does not show the words in History, so they join too.
   */
  async keepWords(text: string): Promise<string | null> {
    if (this.snapshot.conflict === null || !this.isEditing()) return null
    // With no entry yet there is no Draft to read. The create and the rule of the base decide.
    if (this.snapshot.sessionId === null) {
      this.dismissConflict()
      this.edit(text)
      this.saver.flush()
      return text
    }
    const base = this.baseText
    const newest = await this.readNewestDraft()
    if (newest === null || !this.isEditing()) return null
    const history = this.wordsInHistory
    const mine = history === null || holdsEachWordOf(text, history) ? text : mergeDraft(base, history, text)
    const merged = mergeDraft(base, mine, newest)
    this.dismissConflict()
    this.edit(merged)
    this.saver.flush()
    return merged
  }

  async takeNewestWords(): Promise<string | null> {
    if (this.unsettledText() !== null) return null
    if (this.snapshot.sessionId === null) {
      this.dismissConflict()
      return ''
    }
    const newest = await this.readNewestDraft()
    if (newest !== null) this.dismissConflict()
    return newest
  }

  /** The newest Draft, and the base moves to it. Null when the read failed, or when the person or a reply changed something during the read. */
  private async readNewestDraft(): Promise<string | null> {
    const id = this.snapshot.sessionId
    if (!id) return null
    const repliesBefore = this.replyCount
    const unsettledBefore = this.unsettledText()
    const newest = await this.readSessionNow(id).catch(() => {
      Notification.error('The newest words did not load. Try again.')
      return null
    })
    if (!newest || this.unsettledText() !== unsettledBefore || this.replyCount !== repliesBefore) return null
    this.moveBaseToDraftIn(newest)
    return this.draftTextIn(newest)
  }

  private get isRaw() {
    return this.target.channel === 'raw'
  }

  private moveBaseToDraftIn(session: SessionDto) {
    this.moveBaseTo(this.isRaw ? session.rawDraftRevision : session.cleanedDraftRevision, this.draftTextIn(session))
  }

  private draftTextIn(session: SessionDto) {
    return this.isRaw ? session.rawText : (session.cleanedText ?? '')
  }

  moveBaseTo(draftRevision: number, draftText: string) {
    this.baseText = draftText
    this.update({ baseRevision: draftRevision })
    if (this.ownKey !== null) this.keep()
  }

  /** The key holds the text that the notice of this saver showed, so a discard removes no other words. */
  discard() {
    this.isDiscarded = true
    this.stop()
    if (this.ownKey !== null) removeFromOutbox(this.ownKey)
    this.ownKey = null
    for (const [key, saver] of savers) {
      if (saver === this) savers.delete(key)
    }
    helpers.delete(this)
    this.lock.leave(this)
    this.lock.releaseIfDone(this.lockIsFree())
    refreshDraftNotices()
  }

  subscribe = (listener: () => void) => {
    this.listeners.add(listener)
    refreshDraftNotices()
    this.lock.askForTheEntry()
    return () => {
      this.listeners.delete(listener)
      // The words of this notice were only on the page that closed.
      if (this.listeners.size === 0 && this.snapshot.conflict === 'discardedInAnotherTab') this.snapshot = { ...this.snapshot, conflict: null }
      refreshDraftNotices()
      this.releaseLockIfDone()
    }
  }

  getSnapshot = () => this.snapshot

  private update(patch: Partial<DraftSnapshot>) {
    this.snapshot = { ...this.snapshot, ...patch }
    for (const listener of this.listeners) listener()
    refreshDraftNotices()
  }

  writesKey(storageKey: string) {
    return this.ownKey === storageKey
  }

  /** Edit here: this tab takes the entry from the tab that edits it. */
  takeHere() {
    this.lock.takeHere()
  }

  /** A saver that stopped at a sign-out still keeps a late key in storage, so the words return at the next sign-in. */
  private isEditing() {
    return this.snapshot.editing === 'here' && this.lock.isEditingHere()
  }

  /** A saver holds nothing when its words are sent, and no notice or page needs it. */
  isDone() {
    return this.unsettledText() === null && this.heldWords === null && this.snapshot.conflict === null && !this.snapshot.movedFromDeletedEntry && this.listeners.size === 0 && this.newWordsListeners.size === 0 && !this.saver.isSending()
  }

  private lockIsFree() {
    return [...savers.values(), ...helpers].every((saver) => saver.lock !== this.lock || saver.isDone())
  }

  private releaseLockIfDone() {
    if (this.isHelper || !this.isDone() || !this.lockIsFree()) return
    for (const [key, saver] of savers) if (saver.lock === this.lock && saver.isDone()) savers.delete(key)
    this.lock.releaseIfDone(true)
  }

  // LockParty

  handOver(): HandedEntry | null {
    const change = this.saver.newestUnsettled()
    const entry = change === null && this.heldWords === null && this.wordsInHistory === null ? null : this.outboxEntry(change)
    // Words that a full storage left only in memory go to a key now if storage has room, so a crash of each tab keeps them.
    const handed = entry === null ? null : { storageKey: this.ownKey ?? writeToOutbox(this.target.owner, null, entry), entry }
    // The tab that takes the entry goes on from these words and from this key, so this saver holds and writes nothing more.
    this.ownKey = null
    this.heldWords = null
    this.wordsInHistory = null
    this.saver.pause()
    this.lostInARow = 0
    this.readsBeforeNextSave = false
    this.update({ editing: 'elsewhere', conflict: null, waitsForAnotherWriter: false, movedFromDeletedEntry: false })
    if (this.isHelper) this.forget()
    return handed
  }

  /** The words to take: those that the last editor handed over, and each key of the draft that no saver of this tab writes. The newest go on the page, and each other goes with its own base. */
  takeOver(handed: HandOver | null) {
    if (this.isHelper || this.isStopped) return []
    const key = draftKey(this.target)
    const ofThisDraft = (handed?.drafts ?? []).filter(({ entry }) => entry.key === key)
    const handedKeys = new Set(ofThisDraft.map(({ storageKey }) => storageKey))
    const stored = readOutbox(this.target.owner).filter(({ storageKey, entry }) => entry.key === key && !handedKeys.has(storageKey) && !isKeyOfASaverHere(storageKey))
    const toTake = [...ofThisDraft, ...stored]
    // A state that another of these states took in has its words in that state, so they go once.
    const takenIn = new Set(toTake.flatMap(({ entry }) => entry.takenStates ?? []))
    const [main, ...others] = toTake.filter(({ entry }) => !takenIn.has(entry.stateId ?? '')).sort(newestFirst)
    const wasElsewhere = this.snapshot.editing === 'elsewhere'
    if (!main && wasElsewhere) {
      void this.startFromTheDraft()
      return []
    }
    // A page that edits only from now opens the words of the saver when it opens its editor.
    this.update({ editing: 'here' })
    if (main) this.adopt(main.entry, main.storageKey)
    for (const other of others) this.helpWith(other)
    if (this.heldWords !== null && this.newWordsListeners.size === 0) void this.joinHoldWithNoPage()
    return statesOf(ofThisDraft)
  }

  /** Words that a frozen tab or a tab with no room in storage handed over after this tab started. A state that this tab took from storage goes once. */
  takeLateHandOver(handed: HandOver) {
    if (this.isHelper || this.isStopped) return []
    const ofThisDraft = handed.drafts.filter(({ entry }) => entry.key === draftKey(this.target))
    for (const one of ofThisDraft) if (!this.takenStates.has(one.entry.stateId ?? '')) this.helpWith(withKeyToTake(one))
    return statesOf(ofThisDraft)
  }

  takeDraftsOfNoParty(drafts: HandedEntry[]) {
    if (this.isHelper || this.isStopped) return []
    for (const one of drafts) if (!this.takenStates.has(one.entry.stateId ?? '')) this.helpWith(withKeyToTake(one))
    return statesOf(drafts)
  }

  isShown() {
    return this.listeners.size > 0
  }

  noteEditingPlace() {
    const place = this.lock.editingPlace
    if (place === 'here') return
    this.update({ editing: place, wordsOfTheEditor: place === 'elsewhere' ? this.wordsInKeyOfTheEditor() : null })
  }

  /** A page that shows the entry with no edit shows the words of the tab that edits. */
  refreshWordsOfTheEditor() {
    if (this.snapshot.editing !== 'elsewhere') return
    const words = this.wordsInKeyOfTheEditor()
    if (words === this.snapshot.wordsOfTheEditor) return
    const sessionId = this.snapshot.sessionId
    if (words !== null || sessionId === null) {
      this.update({ wordsOfTheEditor: words })
      return
    }
    // With no unsent words, the words of the editor are in the Draft. The page shows the last words until the Draft is read, so the older Draft does not show for a moment.
    void this.queryClient.invalidateQueries({ queryKey: SessionKeys.detail(sessionId), exact: true }).then(() => {
      // In the queue of the query, so the page has the read Draft when the last words go.
      notifyManager.schedule(() => {
        if (this.snapshot.editing === 'elsewhere' && this.wordsInKeyOfTheEditor() === null) this.update({ wordsOfTheEditor: null })
      })
    })
  }

  private wordsInKeyOfTheEditor() {
    const [newest] = readOutbox(this.target.owner)
      // A key that this tab handed over is now the key of the editor, so the name of the key does not tell.
      .filter(({ storageKey, entry }) => entry.key === draftKey(this.target) && !isKeyOfASaverHere(storageKey))
      .sort(newestFirst)
    return newest?.entry.change?.value ?? null
  }

  private forget() {
    helpers.delete(this)
    this.lock.leave(this)
    this.stop()
  }

  /** The words of the last editor, with its base, its change in flight, and its newest change. The next write of this saver replaces `storageKey`. */
  private adopt(entry: OutboxEntry, storageKey: string | null) {
    this.noteTaken(entry)
    this.ownKey = storageKey
    // A key that holds these words stays when a write of the same words finds no room, so a take keeps the words in storage.
    const stored = storageKey === null ? undefined : readOutbox(this.target.owner).find((one) => one.storageKey === storageKey)
    this.kept = stored?.entry.stateId && wordsOfAState(stored.entry) === wordsOfAState(entry) ? { storageKey: stored.storageKey, stateId: stored.entry.stateId, words: wordsOfAState(entry) } : null
    this.target = { ...this.target, sessionId: entry.sessionId ?? this.target.sessionId }
    this.baseText = entry.baseText
    this.readsBeforeNextSave = entry.readsFirst ?? false
    const held = [entry.held ?? null, this.heldWords].filter((text) => text !== null)
    this.heldWords = held.length > 0 ? held.reduce(joinWords) : null
    this.reportsArrival = this.heldWords !== null || (entry.change !== null && !entry.refusal)
    this.update({ sessionId: entry.sessionId ?? this.snapshot.sessionId, baseRevision: entry.baseRevision, movedFromDeletedEntry: entry.movedFromDeletedEntry ?? false })
    this.saver.adopt(queueOf(entry))
    if (entry.wordsInHistory) this.noteWordsOnlyInHistory(entry.wordsInHistory)
    this.keep()
  }

  /** A state that this saver took, and each state that it took in, go once. */
  private noteTaken({ stateId, takenStates }: OutboxEntry) {
    if (stateId) this.takenStates.add(stateId)
    for (const state of takenStates ?? []) this.takenStates.add(state)
  }

  /** Words of another tab that are not the words of this saver go with their own base and channel, as that tab would have sent them. */
  private helpWith(taken: HandedEntry) {
    this.noteTaken(taken.entry)
    const target = { ...this.target, channel: taken.entry.channel, sessionId: taken.entry.sessionId, baseRevision: taken.entry.baseRevision, baseText: taken.entry.baseText }
    helpers.add(new DraftSaver(target, this.queryClient, { taken }))
  }

  /** This tab takes an entry from another tab that holds no unsent words, so it reads the Draft first, and the page opens the read. With no read, the base stays, and the rule of the base decides. */
  private async startFromTheDraft() {
    this.update({ editing: 'waiting' })
    const id = this.snapshot.sessionId
    // Offline, a read waits for the network, and the person must not wait for it.
    const limit = new Promise<null>((resolve) => setTimeout(() => resolve(null), takeReadLimitMs))
    const read = id ? await Promise.race([this.readSessionNow(id).catch(() => null), limit]) : null
    if (this.isStopped || this.lock.editingPlace !== 'here') return
    if (read) this.moveBaseToDraftIn(read)
    this.update({ editing: 'here' })
    if (this.heldWords !== null && this.newWordsListeners.size === 0) void this.joinHoldWithNoPage()
    // A saver with no page and no words, such as one that a load made for a key of another tab, must not keep the entry from a page.
    this.releaseLockIfDone()
  }

  private async sessionIdForSave(signal: AbortSignal) {
    if (this.snapshot.sessionId) return this.snapshot.sessionId
    const location = await this.target.location?.()
    const created = await addSession({
      body: {
        clientSessionId: this.target.clientSessionId,
        startedAt: this.target.startedAt,
        latitude: location?.latitude ?? null,
        longitude: location?.longitude ?? null,
      },
      signal,
      options: { suppressErrorToast: true },
    })
    this.afterCreate(created)
    return created.id
  }

  private afterCreate(created: SessionDto) {
    pointAtCreatedSession(this.target.clientSessionId, created.id)
    // A replay of the create can bring words that this page never showed, also a chunk of a recording. The base stays, so the Server answers `Lost` to a next save over them.
    this.update({ sessionId: created.id })
    this.queryClient.setQueryData(SessionKeys.detail(created.id), created)
    void refreshTimelineHead(this.queryClient)
  }

  private async send(change: Stamped<string>, signal: AbortSignal): Promise<SaveAnswer> {
    if (!this.isEditing()) throw new Error('Another tab edits this entry.')
    try {
      return await this.saveAndRead(change, signal)
    } catch (error) {
      // The delete of this tab, or a discard at logout, is the wish of the person. The words must not come back as a new entry.
      if (!isARowThatIsGone(error) || this.isStopped || this.isBeingDeleted || !this.isEditing()) throw error
      if (this.isOwnKeyGone()) return { reply: null, newest: null }
      this.moveToNewEntry(change)
      return this.saveAndRead(change, signal)
    }
  }

  /** A failed read throws, so the save goes again and gets the same answer. A later save waits for the read, because its base comes from the read. */
  private async saveAndRead(change: Stamped<string>, signal: AbortSignal): Promise<SaveAnswer> {
    if (this.isOwnKeyGone()) return { reply: null, newest: await this.readForAnswer(signal) }
    if (this.readsBeforeNextSave) return { reply: null, newest: await this.readForAnswer(signal), isReadAfterLoss: true }
    const reply = await this.saveWords(change, signal)
    return { reply, newest: reply.outcome === 'Kept' ? await this.readForAnswer(signal) : null }
  }

  /** A sign-out in another tab can discard the key of this saver. If the person typed since, the key is new and this is false. */
  private isOwnKeyGone() {
    return this.ownKey !== null && !this.snapshot.isOnlyInMemory && !isKeyStored(this.ownKey)
  }

  /** New words on this page are new text, and they get a key of their own. */
  private forgetAKeyThatIsGone() {
    if (this.isOwnKeyGone()) this.ownKey = null
  }

  private async readForAnswer(signal: AbortSignal) {
    const id = this.snapshot.sessionId
    if (!id) return null
    return this.readSessionNow(id, signal).catch(asReadFailure)
  }

  /**
   * A read of the server that no other read answers. In the query cache, a second read or a stop cancels the first read,
   * and the first read then gives the older data of the cache. An older base makes the next save `Lost`, and its merge shows words twice.
   */
  private async readSessionNow(id: string, signal?: AbortSignal) {
    // A poll that started before the save can still run, and its older answer must not replace this read in the cache.
    await this.queryClient.cancelQueries({ queryKey: SessionKeys.detail(id), exact: true })
    const session = await readSession(id, signal)
    this.queryClient.setQueryData(SessionKeys.detail(id), session)
    return session
  }

  private async saveWords(change: Stamped<string>, signal: AbortSignal) {
    const id = await this.sessionIdForSave(signal)
    const body = { text: change.value, baseRevision: this.snapshot.baseRevision, savedAt: change.savedAt }
    const options = { suppressErrorToast: true }
    return this.isRaw
      ? saveRawDraft({ path: { id }, body, signal, options })
      : saveCleanedDraft({ path: { id }, body, signal, options })
  }

  /** The words go on as a new entry. With no room in storage, they are only in this page. */
  private moveToNewEntry(change: Stamped<string>) {
    const clientSessionId = newUuid()
    pointAtNewEntry(this.target.clientSessionId, clientSessionId)
    const oldLock = this.lock
    this.target = { ...this.target, clientSessionId, channel: 'raw', sessionId: null, baseRevision: 0 }
    this.lock.leave(this)
    this.lock = lockOfEntry(this.target.owner, clientSessionId, true)
    this.lock.join(this)
    if (!this.isHelper) savers.set(draftKey(this.target), this)
    oldLock.releaseIfDone(this.lockIsFreeOf(oldLock))
    this.wordsInHistory = null
    // A new entry is empty and has no other writer, so a loss on the old entry asks for no read and no wait.
    this.baseText = ''
    this.lostInARow = 0
    this.readsBeforeNextSave = false
    this.update({ sessionId: null, baseRevision: 0, conflict: null, movedFromDeletedEntry: true, waitsForAnotherWriter: false })
    this.keep(change)
  }

  private lockIsFreeOf(lock: EntryLock) {
    return [...savers.values(), ...helpers].every((saver) => saver.lock !== lock || saver.isDone())
  }

  private handleReply({ reply, newest, isReadAfterLoss }: SaveAnswer, change: Stamped<string>) {
    this.replyCount += 1
    // With no reply, each later save of this queue also comes from the key that is gone.
    if (reply) this.forgetAKeyThatIsGone()
    const id = this.snapshot.sessionId
    if (id && reply) void this.queryClient.invalidateQueries({ queryKey: SessionKeys.revisions(id, this.target.channel) })
    if (reply?.outcome === 'Lost') {
      this.readBeforeNextSave(change)
      return
    }
    if (reply) {
      this.lostInARow = 0
      if (this.snapshot.waitsForAnotherWriter) this.update({ waitsForAnotherWriter: false })
    }
    if (newest && id && this.isRaw) this.patchRow(id, newest.rawText, newest)
    if (isReadAfterLoss) {
      this.readsBeforeNextSave = false
      if (newest) this.mergeIntoRead(change, newest)
      return
    }
    if (reply && !newest) {
      this.noteWordsAreTheDraft(change.value, reply.draftRevision ?? this.snapshot.baseRevision, change)
      if (id) this.keepSessionCurrent(id, reply, change.value)
      return
    }
    const fate = newest ? fateOfWords(change.value, this.draftTextIn(newest)) : 'onlyInHistory'
    if (fate === 'areTheDraft' && newest) this.noteWordsAreTheDraft(change.value, this.isRaw ? newest.rawDraftRevision : newest.cleanedDraftRevision, change)
    else if (fate === 'areInTheDraft' && newest) this.noteDraftHoldsMoreThan(change, newest)
    else if (reply) this.noteWordsOnlyInHistory(change.value)
    else if (this.listeners.size > 0) this.update({ conflict: 'discardedInAnotherTab' })
  }

  /** A lost save does not go again. Its words stay in the queue, and the next send reads the Draft first. After 3 losses in a row, that read waits. */
  private readBeforeNextSave(change: Stamped<string>) {
    this.lostInARow += 1
    this.readsBeforeNextSave = true
    // A newer edit holds the words of the lost save, and it goes next.
    if (this.saver.newestUnsettled() === change) this.saver.edit(change.value, change.savedAt)
    this.saver.flush()
    const wait = waitAfterLosses(this.lostInARow, Math.random())
    if (wait > 0) this.saver.holdFor(wait)
    this.update({ waitsForAnotherWriter: wait > 0 })
  }

  /** The change of the person since the base goes into the text of the read, one time, and the next save goes from the number of the read. A page shows the merge at once, so a key typed next does not save the older text. */
  private mergeIntoRead(change: Stamped<string>, newest: SessionDto) {
    if (!this.isEditing()) return
    const mine = this.unsettledText() ?? change.value
    const theirs = this.draftTextIn(newest)
    if (mine === theirs) {
      this.noteWordsAreTheDraft(theirs, this.isRaw ? newest.rawDraftRevision : newest.cleanedDraftRevision)
      return
    }
    const merged = mergeDraft(this.baseText, mine, theirs)
    this.moveBaseToDraftIn(newest)
    this.wordsInHistory = null
    this.update({ conflict: null })
    if (merged === theirs) {
      // The read holds the change of the person, so nothing goes to the Server. Newer typing holds older words, and the text of the read takes its place.
      const newer = this.saver.newestUnsettled()
      if (newer !== null && newer !== change) this.saver.edit(theirs)
      this.wordsOnServer = theirs
      this.noteArrival()
      this.update({ waitsForAnotherWriter: false })
      this.showNewWords(theirs, 'draftHoldsMore')
      return
    }
    this.saver.edit(merged, stampAfter(change.savedAt, this.saver.newestUnsettled()?.savedAt ?? change.savedAt))
    this.saver.flush()
    if (this.listeners.size === 0) this.reportsArrival = true
    if (merged !== mine) this.showNewWords(merged, 'mergedIntoNewest')
  }

  /** Only a read or a save that holds these words moves the base, so a later save cannot push a chunk of a recording out of the Draft. */
  private noteWordsAreTheDraft(words: string, draftRevision: number, change: Stamped<string> | null = null) {
    this.wordsOnServer = words
    this.wordsInHistory = null
    this.baseText = change === null ? words : change.value
    this.noteArrival()
    this.update({ baseRevision: draftRevision, conflict: null, waitsForAnotherWriter: false })
  }

  /**
   * The words of this save are safe, and the Draft holds more. The page takes the Draft. If the person typed since, this
   * save is the base of that typing, so only the typing joins the Draft, and the words of this save do not show twice.
   */
  private noteDraftHoldsMoreThan(change: Stamped<string>, newest: SessionDto) {
    this.noteArrival()
    // A newer change can hold the same words, as after a delete of the words typed since. It still goes after this save, so it merges too.
    const typed = this.saver.newestUnsettled()
    if (typed !== null && typed !== change) {
      this.baseText = change.value
      this.mergeIntoRead(change, newest)
      return
    }
    if (typed !== change) return
    const draft = this.draftTextIn(newest)
    this.wordsOnServer = draft
    this.wordsInHistory = null
    this.moveBaseToDraftIn(newest)
    this.update({ conflict: null })
    this.showNewWords(draft, 'draftHoldsMore')
  }

  private noteArrival() {
    if (!this.reportsArrival) return
    this.reportsArrival = false
    this.hasUnseenJoin = this.listeners.size === 0
  }

  private noteWordsOnlyInHistory(words: string) {
    this.wordsInHistory = words
    this.update({ conflict: 'wordsMovedToHistory' })
  }

  private keepSessionCurrent(id: string, reply: DraftSaveResultDto, words: string) {
    const key = SessionKeys.detail(id)
    const isLoaded = this.queryClient.getQueryData(key) !== undefined
    void this.queryClient.cancelQueries({ queryKey: key, exact: true }).then(() => {
      if (!isLoaded) void this.queryClient.invalidateQueries({ queryKey: key, exact: true })
    })
    this.queryClient.setQueryData<SessionDto>(key, (session) => {
      if (!session) return session
      const draftRevision = reply.draftRevision ?? session.rawDraftRevision
      const draft = this.isRaw
        ? { rawLatestRevision: reply.latestRevision, rawDraftRevision: draftRevision, rawText: words, rawSavedAt: reply.savedAt, personIds: mentionedPersonIds(words, session.cleanedText) }
        : { cleanedLatestRevision: reply.latestRevision, cleanedDraftRevision: draftRevision, cleanedText: words, cleanedSavedAt: reply.savedAt, personIds: mentionedPersonIds(session.rawText, words) }
      return { ...session, ...draft, cleanupStatus: reply.cleanupStatus, version: reply.version }
    })
    if (this.isRaw) this.patchRow(id, words, reply)
  }

  private patchRow(id: string, words: string, current: Pick<SessionDto, 'cleanupStatus' | 'version'>) {
    const cleaned = this.queryClient.getQueryData<SessionDto>(SessionKeys.detail(id))?.cleanedText ?? null
    patchTimelineRow(this.queryClient, id, {
      preview: plainPreviewOf(words, previewLength),
      personIds: mentionedPersonIds(words, cleaned),
      cleanupStatus: current.cleanupStatus,
      version: current.version,
    })
  }

  /**
   * One key holds all the text of this saver, with its base. A write replaces the older key. A failed write removes it when it holds other words, so the words are only in this page, and its notice says so.
   * Only the tab that edits writes words to send. A hold is the words of this tab, so it stays in a key in any tab.
   */
  private keep(fallback: Stamped<string> | null = null) {
    if (this.isDiscarded || this.isOwnKeyGone()) return
    const change = this.saver.newestUnsettled() ?? fallback
    if (change !== null && !this.isEditing()) return
    this.stateId = nextStateId()
    if (change === null && this.heldWords === null && this.wordsInHistory === null) {
      if (this.ownKey !== null) removeFromOutbox(this.ownKey)
      this.ownKey = null
      this.noteOutboxWrite(true)
      this.releaseLockIfDone()
      return
    }
    const entry = this.outboxEntry(change)
    const written = writeToOutbox(this.target.owner, this.ownKey, entry)
    const kept = this.kept
    if (written === null && kept !== null && kept.storageKey === this.ownKey && kept.words === wordsOfAState(entry)) {
      // The key holds these words on this base, so it stays as the state of this saver.
      this.stateId = kept.stateId
      this.noteOutboxWrite(true)
      return
    }
    // A key holds the newest words or nothing. A tab that took older words would send them again on their base, so they would show twice.
    if (written === null && this.ownKey !== null) removeFromOutbox(this.ownKey)
    this.ownKey = written
    this.kept = written === null ? null : { storageKey: written, stateId: this.stateId, words: wordsOfAState(entry) }
    this.noteOutboxWrite(written !== null)
  }

  private noteOutboxWrite(isWritten: boolean) {
    if (this.snapshot.isOnlyInMemory === isWritten) this.update({ isOnlyInMemory: !isWritten })
    else if (!isWritten) refreshDraftNotices()
  }

  private outboxEntry(change: Stamped<string> | null): OutboxEntry {
    const inFlight = this.saver.inFlight()
    return {
      key: draftKey(this.target),
      channel: this.target.channel,
      clientSessionId: this.target.clientSessionId,
      startedAt: this.target.startedAt,
      sessionId: this.snapshot.sessionId,
      baseRevision: this.snapshot.baseRevision,
      baseText: this.baseText,
      change,
      sent: inFlight !== null && inFlight !== change ? inFlight : null,
      readsFirst: this.readsBeforeNextSave,
      held: this.heldWords,
      wordsInHistory: this.wordsInHistory,
      refusal: this.saver.refusalMessage(),
      movedFromDeletedEntry: this.snapshot.movedFromDeletedEntry,
      stateId: this.stateId,
      takenStates: [...this.takenStates],
    }
  }
}

const savers = new Map<string, DraftSaver>()

/** Each saver of this tab names its states from one count, so two savers of one entry never name a state the same. */
let stateCount = 0
const nextStateId = () => `${thisTab}.${(stateCount += 1)}`

/** Savers that finish the words of a tab that left beside the saver of this tab. Each goes with its own base, as that tab would have. */
const helpers = new Set<DraftSaver>()

/** A tab that leaves takes no more text, also when the answer of another tab comes after the leave. */
let hasStopped = false

const draftKey = ({ clientSessionId, channel }: Pick<DraftTarget, 'clientSessionId' | 'channel'>) =>
  `${clientSessionId}.${channel}`

const newestFirst = (one: HandedEntry, other: HandedEntry) =>
  (other.entry.change?.savedAt ?? other.entry.startedAt).localeCompare(one.entry.change?.savedAt ?? one.entry.startedAt)

const isKeyOfASaverHere = (storageKey: string | null) => storageKey !== null && everySaver().some((saver) => saver.writesKey(storageKey))
/** A handed key that is gone, or that a saver of this tab writes now, is not the key of these words. */
const withKeyToTake = (handed: HandedEntry): HandedEntry => ({ ...handed, storageKey: handed.storageKey !== null && isKeyStored(handed.storageKey) && !isKeyOfASaverHere(handed.storageKey) ? handed.storageKey : null })
const statesOf = (drafts: HandedEntry[]) => drafts.flatMap(({ entry }) => entry.stateId ?? [])

/** The saver of a draft in this tab. It edits when this tab holds the lock of the entry, and it starts from the words and base of the last editor. */
export function draftSaverFor(target: DraftTarget, queryClient: QueryClient, isNewEntry = false): DraftSaver {
  const key = draftKey(target)
  const existing = savers.get(key)
  if (existing) return existing
  const saver = new DraftSaver(target, queryClient, { isNewEntry })
  savers.set(key, saver)
  return saver
}

export function discardDraftSavers(clientSessionId: string) {
  for (const channel of ['raw', 'cleaned'] as const) {
    savers.get(draftKey({ clientSessionId, channel }))?.discard()
  }
}

export function noteDeleteOfDrafts(clientSessionId: string, isRunning: boolean) {
  for (const channel of ['raw', 'cleaned'] as const) {
    savers.get(draftKey({ clientSessionId, channel }))?.noteDelete(isRunning)
  }
}

/** Each draft with words in a key of another tab gets a saver here. It takes the words when the lock of the entry comes to this tab, after the tab that edits left. */
export function resumeUnsentDrafts(owner: string, queryClient: QueryClient) {
  if (hasStopped) return
  for (const { writer, entry } of readOutbox(owner).sort(newestFirst)) {
    if (writer === thisTab || savers.has(entry.key)) continue
    draftSaverFor({ owner, channel: entry.channel, clientSessionId: entry.clientSessionId, startedAt: entry.startedAt, sessionId: entry.sessionId, baseRevision: entry.baseRevision, baseText: entry.baseText }, queryClient)
  }
  refreshWordsOfEditors()
  refreshDraftNotices()
}

export function refreshWordsOfEditors() {
  for (const saver of savers.values()) saver.refreshWordsOfTheEditor()
}

const everySaver = () => [...new Set([...savers.values(), ...helpers])]

let shownNotices: DraftNotice[] = []
const noticeListeners = new Set<() => void>()

function refreshDraftNotices() {
  shownNotices = everySaver().flatMap((saver) => saver.notice() ?? [])
  for (const listener of noticeListeners) listener()
}

export const draftNotices = () => shownNotices

export function subscribeToDraftNotices(listener: () => void) {
  noticeListeners.add(listener)
  return () => {
    noticeListeners.delete(listener)
  }
}

export const isAnyWordOnlyInMemory = () => everySaver().some((saver) => saver.isHoldingWordsOnlyInMemory())

export async function settleDraftSavers() {
  const settled = await Promise.all(everySaver().map((saver) => saver.settle()))
  return settled.every(Boolean)
}

export function stopDraftSavers() {
  hasStopped = true
  for (const saver of everySaver()) saver.stop()
  closeEntryLocks()
}

/** A key that only keeps a notice about words in History holds words that the Server has, so no key stays for the next person of this device. */
export function removeNoticesOfWordsInHistory(owner: string | null) {
  if (owner === null) return
  for (const { storageKey, entry } of readOutbox(owner)) {
    if (entry.change === null && !entry.held) removeFromOutbox(storageKey)
  }
}

export const wordsOfThisTabOnlyInMemory = () =>
  everySaver().flatMap((saver) => (saver.isHoldingWordsOnlyInMemory() ? (saver.unsentWords() ?? []) : []))

export interface UnsentWords {
  words: string
  isRefused: boolean
  isOnlyInMemory: boolean
  discard: () => void
}

/** All the words that a sign-out leaves on this device or loses. A discard removes only the text of this list. Words typed after the list stay in their key, and they return with a notice. */
export function unsentWordsOnThisDevice(owner: string | null, otherTabs: OtherTabReport[]): UnsentWords[] {
  const ofThisTab = everySaver().flatMap((saver) => {
    const words = saver.unsentWords()
    return words === null ? [] : { words, isRefused: saver.getSnapshot().status.kind === 'refused', isOnlyInMemory: saver.isHoldingWordsOnlyInMemory(), discard: () => saver.unsentWords() === words && saver.discard() }
  })
  // A key that a saver of this tab took from another tab is in the list of this tab, with the words of that saver.
  const inKeysOfOtherTabs = (owner === null ? [] : readOutbox(owner)).filter(({ writer, storageKey }) => writer !== thisTab && !isKeyOfASaverHere(storageKey)).flatMap(({ storageKey, entry }) => {
    const unsent = [entry.change?.value ?? null, entry.held ?? null].filter((text) => text !== null)
    return unsent.length === 0 ? [] : { words: unsent.reduce(joinWords), isRefused: Boolean(entry.refusal), isOnlyInMemory: false, discard: () => removeFromOutbox(storageKey) }
  })
  const inMemoryOfOtherTabs = otherTabs.flatMap(({ wordsOnlyInMemory }) => wordsOnlyInMemory.map((words) => ({ words, isRefused: false, isOnlyInMemory: true, discard: () => undefined })))
  return [...ofThisTab, ...inKeysOfOtherTabs, ...inMemoryOfOtherTabs]
}
