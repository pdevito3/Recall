import { isCancelledError, type QueryClient } from '@tanstack/react-query'

import type { DraftSaveResultDto } from '@/api/generated/models/types/draft-save-result-dto'
import type { SessionDto } from '@/api/generated/models/types/session-dto'
import { addSession } from '@/api/generated/sessions/clients/add-session'
import { getCleanedRevision } from '@/api/generated/sessions/clients/get-cleaned-revision'
import { getCleanedRevisionPage } from '@/api/generated/sessions/clients/get-cleaned-revision-page'
import { getRawRevision } from '@/api/generated/sessions/clients/get-raw-revision'
import { getRawRevisionPage } from '@/api/generated/sessions/clients/get-raw-revision-page'
import { saveCleanedDraft } from '@/api/generated/sessions/clients/save-cleaned-draft'
import { saveRawDraft } from '@/api/generated/sessions/clients/save-raw-draft'
import { Notification } from '@/components/notification'
import { newUuid } from '@/lib/new-uuid'

import { sessionQueryOptions } from '../apis/get-session'
import { SessionKeys, type DraftChannel } from '../apis/session.keys'
import { patchTimelineRow, refreshTimelineHead } from '../apis/timeline-cache'
import { holdsEachWordOf, joinWords, mentionedPersonIds, mergeWords, plainPreviewOf } from '../editor/content'
import { mergeDraft } from '../editor/merge-draft'
import { pointAtCreatedSession, pointAtNewEntry } from './composer-pointer'
import { prefixOfOutbox, readOutbox, removeFromOutbox, storedEntriesIn, writeToOutbox, type OutboxEntry, type StoredEntry } from './outbox'
import { queueWithRefusal, queueWithUnanswered, type SaveStatus, type Stamped } from './save-queue'
import { isARowThatIsGone, isFinalRefusal, SerialSaver } from './serial-saver'
import { hasWriterLeft, isKeyStored, noteCopyOfAKey, takeKeysOfTabsThatLeft, thisTab, watchCopiesOfKeysOfThisTab, type OtherTabReport } from './tab-keys'

export type DraftConflict = 'wordsMovedToHistory' | 'discardedInAnotherTab' | null

export type ShownSaveStatus = SaveStatus | { kind: 'savedInHistory'; at: string } | { kind: 'waitingForAnotherWriter' }

export interface DraftSnapshot {
  status: SaveStatus
  conflict: DraftConflict
  sessionId: string | null
  baseRevision: number
  movedFromDeletedEntry: boolean
  isOnlyInMemory: boolean
  areWordsWithAnotherTab: boolean
  waitsForAnotherWriter: boolean
}

export type DraftNotice =
  | { kind: 'moved'; saver: DraftSaver; sessionId: string | null }
  | { kind: 'joined'; saver: DraftSaver; sessionId: string | null }
  | { kind: 'inHistory'; saver: DraftSaver; sessionId: string | null; words: string }
  | { kind: 'refused'; saver: DraftSaver; reason: string; words: string }
  | { kind: 'waiting'; saver: DraftSaver; words: string }
  | { kind: 'onlyInMemory'; saver: DraftSaver; reason: string | null; canDiscard: boolean; words: string }

export function shownStatusOf({ status, conflict, areWordsWithAnotherTab, waitsForAnotherWriter }: DraftSnapshot): ShownSaveStatus {
  if (waitsForAnotherWriter && status.kind !== 'refused') return { kind: 'waitingForAnotherWriter' }
  if (status.kind !== 'saved') return status
  if (areWordsWithAnotherTab) return { kind: 'idle' }
  if (conflict === null) return status
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

/** A save after a loss needs a new `savedAt`. The same `savedAt` with the same words is a replay, and it gets `Lost` again. */
const stampAfter = (...stamps: string[]) => new Date(Math.max(Date.now(), ...stamps.map((savedAt) => Date.parse(savedAt) + 1).filter(Number.isFinite))).toISOString()

/** Where the words of a save are, by the text of the Draft. A word of these words that is not in the Draft needs the person. */
export type WordsFate = 'areTheDraft' | 'areInTheDraft' | 'onlyInHistory'

export function fateOfWords(words: string, draft: string): WordsFate {
  if (words === draft) return 'areTheDraft'
  return holdsEachWordOf(draft, words) ? 'areInTheDraft' : 'onlyInHistory'
}

/**
 * `reply` is null when no save went: another tab took or discarded the key, or the send was the read after `Lost`. `newest` is the read that a save needs after `Kept` or `Lost`.
 * `ownWordsInDraft` are the words of the newest change of this page that the Draft took after the base, with no answer to this page.
 */
interface SaveAnswer {
  reply: DraftSaveResultDto | null
  newest: SessionDto | null
  isKeyWithAnotherTab?: boolean
  isReadAfterLoss?: boolean
  ownWordsInDraft?: string | null
}

const revisionClients = {
  raw: { page: getRawRevisionPage, one: getRawRevision },
  cleaned: { page: getCleanedRevisionPage, one: getCleanedRevision },
}

/** A change that went to the key of a saver. A tab that copied the key can have sent it, and then only that tab has the answer. */
interface ChangeInKey {
  change: Stamped<string>
  isAnswered: boolean
  /** The place of the change among the changes of this saver, from 1. */
  order: number
}

/** Another tab can copy a key at any moment, so each change that went to the key can come back as a save of that tab. A few are enough, because the base moves with each answer. */
const changesInKeyKept = 20

const isSameChange = (one: Stamped<string>, other: Stamped<string>) => one.value === other.value && one.savedAt === other.savedAt

const doesKeyHoldWords = ({ change, wordsInHistory }: OutboxEntry, words: string) => change?.value === words || wordsInHistory === words

export interface DraftTarget {
  owner: string
  channel: DraftChannel
  clientSessionId: string
  startedAt: string
  sessionId: string | null
  baseRevision: number
  /** The text of the Draft at `baseRevision`, so that a merge takes only the change of the person. Unknown for a key of an older version. */
  baseText?: string | null
  /** Asked at the create, because the person can turn capture off after the first word. */
  location?: () => Promise<{ latitude: number; longitude: number } | null>
}

const pauseInTypingMs = 800
const holdReadLimitMs = 30_000
const holdReadRetryMs = 2_000
const previewLength = 200

export class DraftSaver {
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
  private hasBeenTouched = false
  private hasUnseenJoin = false
  private reportsArrival = false
  private holdRun = 0
  private readonly newWordsListeners = new Set<(words: string, why: WhyNewWords) => void>()
  private isStopped = false
  private isDiscarded = false
  private isBeingDeleted = false
  private baseText: string | null
  private changesInKey: ChangeInKey[] = []
  private changesNoted = 0
  /** A win of a change of this order or later gives the words a base again. Null when no other tab holds the words of this saver. */
  private copiedUpTo: number | null = null
  private lostInARow = 0
  private readsBeforeNextSave = false

  constructor(target: DraftTarget, queryClient: QueryClient, resumed?: StoredEntry | CopyOfAKey) {
    const entry = resumed?.entry
    this.target = target
    this.queryClient = queryClient
    this.baseText = target.baseText ?? (target.baseRevision === 0 && !resumed ? '' : null)
    this.snapshot = {
      status: { kind: 'idle' },
      conflict: null,
      sessionId: target.sessionId,
      baseRevision: target.baseRevision,
      movedFromDeletedEntry: entry?.movedFromDeletedEntry ?? false,
      isOnlyInMemory: false,
      areWordsWithAnotherTab: false,
      waitsForAnotherWriter: false,
    }
    this.saver = new SerialSaver({
      debounceMs: pauseInTypingMs,
      send: (change, signal) => this.send(change, signal),
      onReply: (reply, change) => this.handleReply(reply, change),
      onQueueChange: () => this.keep(),
      initialQueue: entry?.change ? (entry.refusal ? queueWithRefusal(entry.change, entry.refusal) : queueWithUnanswered(entry.change)) : undefined,
    })
    this.snapshot = { ...this.snapshot, status: this.saver.getStatus() }
    this.saver.subscribe(() => this.update({ status: this.saver.getStatus() }))
    if (resumed) {
      this.takeKeyOf(resumed)
      this.keep()
    }
  }

  /** The text of a tab that left moves to a key of this tab. The next write replaces the key of that tab, so the text is in a key at each moment. */
  private takeKeyOf({ storageKey, entry }: StoredEntry | CopyOfAKey) {
    this.hasBeenTouched = true
    this.ownKey = storageKey
    this.heldWords = entry.held ?? null
    // The tab that wrote the key can have sent these words, so they have no base until a save of this saver wins.
    if (entry.change !== null || this.heldWords !== null) this.copiedUpTo = 0
    this.reportsArrival = this.heldWords !== null || (entry.change !== null && !entry.refusal)
    if (entry.wordsInHistory) this.noteWordsOnlyInHistory(entry.wordsInHistory)
    if (this.heldWords !== null) queueMicrotask(() => this.holdRun === 0 && void this.joinHoldWithNoPage())
  }

  /** A page can open before this tab can know that another tab left. That page shows the text of the tab if the person has done nothing on the page. */
  takeEntryIfUntouched(stored: StoredEntry) {
    const { entry } = stored
    // A key that holds only a notice names words that are not on this page. A saver of its own shows them.
    if (this.hasBeenTouched || this.isStopped || entry.refusal || (entry.change === null && entry.wordsInHistory)) return false
    this.baseText = null
    this.update({ sessionId: entry.sessionId ?? this.snapshot.sessionId, baseRevision: entry.baseRevision })
    this.takeKeyOf(stored)
    if (entry.change) {
      this.saver.edit(entry.change.value, entry.change.savedAt)
      this.showNewWords(entry.change.value, 'heldWordsJoined')
    } else this.keep()
    return true
  }

  edit(text: string) {
    this.hasBeenTouched = true
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

  holdInOutbox(text: string) {
    this.hasBeenTouched = true
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
    if (this.heldWords === null) return words
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
    const isCurrent = () => run === this.holdRun && !this.isStopped
    const id = this.snapshot.sessionId
    while (isCurrent() && this.heldWords !== null) {
      const key = SessionKeys.detail(id ?? '')
      const limit = setTimeout(() => void this.queryClient.cancelQueries({ queryKey: key, exact: true }), holdReadLimitMs)
      const session = id === null
        ? null
        : await this.queryClient.fetchQuery({ ...sessionQueryOptions(id), staleTime: 0 }).catch((error: unknown) => (isARowThatIsGone(error) ? null : undefined))
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

  keepsTheSameWordsAs({ key, change, held, wordsInHistory }: OutboxEntry) {
    const own = this.saver.newestUnsettled()
    // A key that another tab took is not a copy of this saver. That tab can leave, and its key is then the only copy.
    return this.ownKey !== null && isKeyStored(this.ownKey) && !this.snapshot.isOnlyInMemory && key === draftKey(this.target) && own?.value === change?.value && own?.savedAt === change?.savedAt && this.heldWords === (held ?? null) && this.wordsInHistory === (wordsInHistory ?? null)
  }

  /**
   * Another tab copied the key of this saver, and a save of that tab can bring its words into the Draft in a change that
   * this saver does not know. So the words have no known base until a save that holds them wins here, and a merge
   * keeps each word that both sides hold once, as the merge of the iOS client does.
   */
  noteCopyInAnotherTab() {
    if (this.unsentWords() === null) return
    this.baseText = null
    this.copiedUpTo = this.changesNoted + (this.heldWords === null ? 0 : 1)
  }

  /** The words of a change that the Draft took are the base, unless another tab copied newer words of this saver. */
  private baseAfterWinOf(change: Stamped<string>): string | null {
    const order = this.changesInKey.find((inKey) => isSameChange(inKey.change, change))?.order
    if (this.copiedUpTo !== null && order !== undefined && order < this.copiedUpTo) return null
    this.copiedUpTo = null
    return change.value
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
   * A notice that stays after a merge: words that left the Draft after a save took them, or words of a tab that another tab discarded.
   * The person asks for the words of the page in the newest Draft. Only the change of the person since the base goes in, so no paragraph shows twice.
   * The answer is the words to show, and null means that nothing changed. A page that opened before the notice came does not show the words in History, so they join too.
   */
  async keepWords(text: string): Promise<string | null> {
    if (this.snapshot.conflict === null) return null
    // With no entry yet there is no Draft to read. The create and the rule of the base decide.
    if (this.snapshot.sessionId === null) {
      this.dismissConflict()
      this.edit(text)
      this.saver.flush()
      return text
    }
    const base = this.baseText
    const newest = await this.readNewestDraft()
    if (newest === null) return null
    const history = this.wordsInHistory
    const mine = history === null || holdsEachWordOf(text, history) ? text : mergeDraft(base, history, text)
    const merged = mergeDraft(base, mine, newest)
    if (base === null) this.baseText = null
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
    const newest = await this.readSessionNow(id).catch((error: unknown) => {
      if (!isCancelledError(error)) Notification.error('The newest words did not load. Try again.')
      return null
    })
    if (!newest || this.unsettledText() !== unsettledBefore || this.replyCount !== repliesBefore) return null
    this.moveBaseToDraftIn(newest)
    return this.isRaw ? newest.rawText : (newest.cleanedText ?? '')
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

  /** Words that two tabs hold can be in the Draft or not, and a read does not tell which. So they keep no base until a save of them wins here. */
  moveBaseTo(draftRevision: number, draftText: string) {
    if (this.copiedUpTo === null) this.baseText = draftText
    this.update({ baseRevision: draftRevision })
  }

  /** The key holds the text that the notice of this saver showed, so a discard removes no other words. A copy in a key of another tab is the same text, and it must not come back. */
  discard() {
    const words = this.unsettledText()
    const key = draftKey(this.target)
    this.isDiscarded = true
    this.stop()
    if (this.ownKey !== null) removeFromOutbox(this.ownKey)
    this.ownKey = null
    for (const { storageKey, entry } of readOutbox(this.target.owner)) {
      if (words !== null && entry.key === key && entry.change?.value === words) removeFromOutbox(storageKey)
    }
    for (const [key, saver] of savers) {
      if (saver === this) savers.delete(key)
    }
    sendingForATabThatLeft.delete(this)
    refreshDraftNotices()
  }

  subscribe = (listener: () => void) => {
    this.listeners.add(listener)
    refreshDraftNotices()
    return () => {
      this.listeners.delete(listener)
      // The words of this notice were only on the page that closed.
      if (this.listeners.size === 0 && this.snapshot.conflict === 'discardedInAnotherTab') this.snapshot = { ...this.snapshot, conflict: null }
      refreshDraftNotices()
    }
  }

  getSnapshot = () => this.snapshot

  private update(patch: Partial<DraftSnapshot>) {
    this.snapshot = { ...this.snapshot, ...patch }
    for (const listener of this.listeners) listener()
    refreshDraftNotices()
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
    try {
      return await this.saveAndRead(change, signal)
    } catch (error) {
      // The delete of this tab, or a discard at logout, is the wish of the person. The words must not come back as a new entry.
      if (!isARowThatIsGone(error) || this.isStopped || this.isBeingDeleted) throw error
      // Another tab has these words. The page says that they may be only there, and a new entry would be a promise with no key behind it.
      if (this.isOwnKeyGone()) return { reply: null, newest: null, isKeyWithAnotherTab: this.isNewestTextInAKey(change) }
      this.moveToNewEntry(change)
      return this.saveAndRead(change, signal)
    }
  }

  /** A failed read throws, so the save goes again and gets the same answer. A later save waits for the read, because its base comes from the read. */
  private async saveAndRead(change: Stamped<string>, signal: AbortSignal): Promise<SaveAnswer> {
    if (this.isOwnKeyGone()) {
      const newest = await this.readForAnswer(signal)
      return { reply: null, newest, isKeyWithAnotherTab: this.isNewestTextInAKey(change) }
    }
    if (this.readsBeforeNextSave) {
      const newest = await this.readForAnswer(signal)
      return { reply: null, newest, isReadAfterLoss: true, ownWordsInDraft: newest && (await this.ownWordsInDraft(newest, signal)) }
    }
    const reply = await this.saveWords(change, signal)
    return { reply, newest: reply.outcome === 'Kept' ? await this.readForAnswer(signal) : null }
  }

  private isNewestTextInAKey(change: Stamped<string>) {
    const newest = this.unsettledText() ?? change.value
    return readOutbox(this.target.owner).some(({ entry }) => doesKeyHoldWords(entry, newest))
  }

  /** A frozen tab can wake after another tab took or discarded its key. If the person typed since, the key is new and this is false. */
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
    const stopRead = () => void this.queryClient.cancelQueries({ queryKey: SessionKeys.detail(id), exact: true })
    signal.addEventListener('abort', stopRead)
    try {
      return await this.readSessionNow(id).catch(asReadFailure)
    } finally {
      signal.removeEventListener('abort', stopRead)
    }
  }

  /**
   * A merge after `Lost` takes the change of the person since the newest Draft that the page went on from. A tab that
   * copied the key of this page can have sent a change of this page, and the Draft took it with no answer here. The
   * Revisions after the base name such a change by its `savedAt` and its words. A Revision of a save that lost also has
   * them, and a replay tells the two apart with no write: SES-8 answers `Kept` only for a save that the Draft took.
   */
  private async ownWordsInDraft(newest: SessionDto, signal: AbortSignal): Promise<string | null> {
    const base = this.snapshot.baseRevision
    const unheard = this.changesInKey.filter(({ isAnswered }) => !isAnswered)
    if (unheard.length === 0 || (this.isRaw ? newest.rawDraftRevision : newest.cleanedDraftRevision) <= base) return null
    const clients = revisionClients[this.target.channel]
    const options = { suppressErrorToast: true }
    const found: { number: number; inKey: ChangeInKey }[] = []
    let cursor: string | undefined
    do {
      const page = await clients.page({ path: { id: newest.id }, query: { cursor }, signal, options }).catch(asReadFailure)
      const after = page.items.filter(({ number }) => number > base)
      for (const { number, savedAt, length } of after) {
        const inKey = unheard.find(({ change }) => Date.parse(change.savedAt) === Date.parse(savedAt) && change.value.length === length)
        if (inKey) found.push({ number, inKey })
      }
      cursor = after.length === page.items.length ? (page.nextCursor ?? undefined) : undefined
    } while (cursor)
    for (const { number, inKey } of found) {
      const { text } = await clients.one({ path: { id: newest.id, number }, signal, options }).catch(asReadFailure)
      if (text !== inKey.change.value) continue
      const { outcome } = await this.saveWords(inKey.change, signal).catch(asReadFailure)
      inKey.isAnswered = true
      if (outcome === 'Kept') return this.baseAfterWinOf(inKey.change)
    }
    return null
  }

  private noteInKey(change: Stamped<string>) {
    if (this.changesInKey.some((inKey) => isSameChange(inKey.change, change))) return
    this.changesNoted += 1
    this.changesInKey = [...this.changesInKey, { change, isAnswered: false, order: this.changesNoted }].slice(-changesInKeyKept)
  }

  private noteAnswerTo(change: Stamped<string>) {
    for (const inKey of this.changesInKey) if (isSameChange(inKey.change, change)) inKey.isAnswered = true
  }

  /** A poll that started before the save can still run, and a read must not take its older answer. */
  private async readSessionNow(id: string) {
    await this.queryClient.cancelQueries({ queryKey: SessionKeys.detail(id), exact: true })
    return this.queryClient.fetchQuery({ ...sessionQueryOptions(id), staleTime: 0 })
  }

  private async saveWords(change: Stamped<string>, signal: AbortSignal) {
    const id = await this.sessionIdForSave(signal)
    const body = { text: change.value, baseRevision: this.snapshot.baseRevision, savedAt: change.savedAt }
    const options = { suppressErrorToast: true }
    return this.isRaw
      ? saveRawDraft({ path: { id }, body, signal, options })
      : saveCleanedDraft({ path: { id }, body, signal, options })
  }

  /** A failed write leaves the older key, which still holds the words. */
  private moveToNewEntry(change: Stamped<string>) {
    const clientSessionId = newUuid()
    pointAtNewEntry(this.target.clientSessionId, clientSessionId)
    this.target = { ...this.target, clientSessionId, channel: 'raw', sessionId: null, baseRevision: 0 }
    savers.set(draftKey(this.target), this)
    this.wordsInHistory = null
    // A new entry is empty and has no other writer, so a loss on the old entry asks for no read and no wait.
    this.baseText = ''
    this.lostInARow = 0
    this.readsBeforeNextSave = false
    this.update({ sessionId: null, baseRevision: 0, conflict: null, movedFromDeletedEntry: true, waitsForAnotherWriter: false })
    this.keep(change)
  }

  private handleReply({ reply, newest, isKeyWithAnotherTab, isReadAfterLoss, ownWordsInDraft }: SaveAnswer, change: Stamped<string>) {
    this.replyCount += 1
    if (reply) this.noteAnswerTo(change)
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
      if (newest) this.mergeIntoRead(change, newest, ownWordsInDraft ?? this.baseText)
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
    else if (isKeyWithAnotherTab) this.update({ areWordsWithAnotherTab: true })
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

  /** The change of the person since `base` goes into the text of the read, one time, and the next save goes from the number of the read. A page shows the merge at once, so a key typed next does not save the older text. */
  private mergeIntoRead(change: Stamped<string>, newest: SessionDto, base = this.baseText) {
    const mine = this.unsettledText() ?? change.value
    const theirs = this.draftTextIn(newest)
    if (mine === theirs) {
      this.noteWordsAreTheDraft(theirs, this.isRaw ? newest.rawDraftRevision : newest.cleanedDraftRevision)
      return
    }
    const merged = mergeDraft(base, mine, theirs)
    this.moveBaseToDraftIn(newest)
    // Words with no known base can come into the Draft by a save of another tab that holds them too, so they keep no base until a save of this saver wins.
    if (base === null) this.baseText = null
    this.wordsInHistory = null
    this.update({ conflict: null })
    if (merged === theirs) {
      // The read holds the change of the person, so nothing goes to the Server. Newer typing holds older words, and the text of the read takes its place.
      if (this.unsettledText() !== change.value) this.saver.edit(theirs)
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
    this.baseText = change === null ? words : this.baseAfterWinOf(change)
    this.noteArrival()
    this.update({ baseRevision: draftRevision, conflict: null, areWordsWithAnotherTab: false, waitsForAnotherWriter: false })
  }

  /**
   * The words of this save are safe, and the Draft holds more. The page takes the Draft. If the person typed since, this
   * save is the base of that typing, so only the typing joins the Draft, and the words of this save do not show twice.
   */
  private noteDraftHoldsMoreThan(change: Stamped<string>, newest: SessionDto) {
    this.noteArrival()
    const typed = this.unsettledText()
    if (typed !== null && typed !== change.value) {
      this.baseText = this.baseAfterWinOf(change)
      this.mergeIntoRead(change, newest)
      return
    }
    if (typed !== change.value) return
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

  /** One key holds all the text of this saver. A write replaces the older key, and a failed write leaves it. */
  private keep(fallback: Stamped<string> | null = null) {
    // A retry must not write back the words of a key that another tab took or discarded.
    if (this.isDiscarded || this.isOwnKeyGone()) return
    const change = this.saver.newestUnsettled() ?? fallback
    if (change === null && this.heldWords === null && this.wordsInHistory === null) {
      if (this.ownKey !== null) removeFromOutbox(this.ownKey)
      this.ownKey = null
      this.noteOutboxWrite(true)
      return
    }
    const written = writeToOutbox(this.target.owner, this.ownKey, this.outboxEntry(change, this.saver.refusalMessage()))
    if (written !== null) this.ownKey = written
    if (written !== null && change !== null) this.noteInKey(change)
    this.noteOutboxWrite(written !== null)
  }

  private noteOutboxWrite(isWritten: boolean) {
    if (this.snapshot.isOnlyInMemory === isWritten) this.update({ isOnlyInMemory: !isWritten })
    else if (!isWritten) refreshDraftNotices()
  }

  private outboxEntry(change: Stamped<string> | null, refusal: string | null): OutboxEntry {
    return {
      key: draftKey(this.target),
      channel: this.target.channel,
      clientSessionId: this.target.clientSessionId,
      startedAt: this.target.startedAt,
      sessionId: this.snapshot.sessionId,
      baseRevision: this.snapshot.baseRevision,
      change,
      held: this.heldWords,
      wordsInHistory: this.wordsInHistory,
      refusal,
      movedFromDeletedEntry: this.snapshot.movedFromDeletedEntry,
    }
  }
}

const savers = new Map<string, DraftSaver>()

/** The text of a tab that left, on a draft that this tab already has a saver for. It finishes that save with its own base, as that tab would have. */
const sendingForATabThatLeft = new Set<DraftSaver>()

/** A tab that leaves takes no more text, also when the answer of another tab comes after the leave. */
let hasStopped = false

watchCopiesOfKeysOfThisTab((key) => savers.get(key)?.noteCopyInAnotherTab())

const draftKey = ({ clientSessionId, channel }: Pick<DraftTarget, 'clientSessionId' | 'channel'>) =>
  `${clientSessionId}.${channel}`

const newestFirst = (one: StoredEntry, other: StoredEntry) =>
  (other.entry.change?.savedAt ?? other.entry.startedAt).localeCompare(one.entry.change?.savedAt ?? one.entry.startedAt)

/** The words of a key that another tab still holds. This saver writes its own key, and the same save from two tabs is a replay. */
type CopyOfAKey = Omit<StoredEntry, 'storageKey'> & { storageKey: null }

function takeEntry(owner: string, stored: StoredEntry, queryClient: QueryClient) {
  // A key of this tab holds the same words, so a second copy adds only a second notice.
  if (everySaver().some((saver) => saver.keepsTheSameWordsAs(stored.entry))) {
    removeFromOutbox(stored.storageKey)
    return
  }
  // A writer that did not answer in time can still live. If it hears, it knows that its words are in two tabs.
  if (stored.writer !== null) noteCopyOfAKey(stored.writer, stored.entry.key)
  const target = { ...stored.entry, owner }
  const existing = savers.get(stored.entry.key)
  if (!existing) savers.set(stored.entry.key, new DraftSaver(target, queryClient, stored))
  else if (!existing.takeEntryIfUntouched(stored)) sendingForATabThatLeft.add(new DraftSaver(target, queryClient, stored))
}

export function draftSaverFor(target: DraftTarget, queryClient: QueryClient): DraftSaver {
  const key = draftKey(target)
  const existing = savers.get(key)
  if (existing) return existing
  // A page that reloads finds its words also when another tab took its key first. A page never opens with less than a key of its entry holds.
  const [newest, ...older] = readOutbox(target.owner)
    .filter(({ writer, entry }) => entry.key === key && writer !== thisTab)
    .sort(newestFirst)
  const resumed = newest && (hasWriterLeft(newest.writer) ? newest : { ...newest, storageKey: null })
  if (resumed && resumed.writer !== null) noteCopyOfAKey(resumed.writer, key)
  const saver = resumed
    ? new DraftSaver({ ...target, sessionId: resumed.entry.sessionId, baseRevision: resumed.entry.baseRevision }, queryClient, resumed)
    : new DraftSaver(target, queryClient)
  savers.set(key, saver)
  for (const stored of older.filter(({ writer }) => hasWriterLeft(writer))) takeEntry(target.owner, stored, queryClient)
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

export const resumeUnsentDrafts = (owner: string, queryClient: QueryClient) =>
  takeKeysOfTabsThatLeft(prefixOfOutbox(owner), (keys) => {
    if (hasStopped) return
    for (const stored of storedEntriesIn(keys).sort(newestFirst)) takeEntry(owner, stored, queryClient)
    refreshDraftNotices()
  })

const everySaver = () => [...new Set([...savers.values(), ...sendingForATabThatLeft])]

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
  const inKeysOfOtherTabs = (owner === null ? [] : readOutbox(owner)).filter(({ writer }) => writer !== thisTab).flatMap(({ storageKey, entry }) => {
    const unsent = [entry.change?.value ?? null, entry.held ?? null].filter((text) => text !== null)
    return unsent.length === 0 ? [] : { words: unsent.reduce(joinWords), isRefused: Boolean(entry.refusal), isOnlyInMemory: false, discard: () => removeFromOutbox(storageKey) }
  })
  const inMemoryOfOtherTabs = otherTabs.flatMap(({ wordsOnlyInMemory }) => wordsOnlyInMemory.map((words) => ({ words, isRefused: false, isOnlyInMemory: true, discard: () => undefined })))
  return withEachTextOnce([...ofThisTab, ...inKeysOfOtherTabs, ...inMemoryOfOtherTabs])
}

/** A page that reloads with a second tab open holds a copy of a key. The person sees the words once, and a discard takes each copy. */
function withEachTextOnce(all: UnsentWords[]): UnsentWords[] {
  const byText = new Map<string, UnsentWords>()
  for (const item of all) {
    const same = byText.get(item.words)
    byText.set(item.words, same ? { ...same, isRefused: same.isRefused || item.isRefused, isOnlyInMemory: same.isOnlyInMemory || item.isOnlyInMemory, discard: () => { same.discard(); item.discard() } } : item)
  }
  return [...byText.values()]
}
