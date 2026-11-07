import { isCancelledError, type QueryClient } from '@tanstack/react-query'

import type { DraftSaveResultDto } from '@/api/generated/models/types/draft-save-result-dto'
import type { SessionDto } from '@/api/generated/models/types/session-dto'
import { addSession } from '@/api/generated/sessions/clients/add-session'
import { saveCleanedDraft } from '@/api/generated/sessions/clients/save-cleaned-draft'
import { saveRawDraft } from '@/api/generated/sessions/clients/save-raw-draft'
import { Notification } from '@/components/notification'
import { newUuid } from '@/lib/new-uuid'

import { sessionQueryOptions } from '../apis/get-session'
import { SessionKeys, type DraftChannel } from '../apis/session.keys'
import { patchTimelineRow, refreshTimelineHead } from '../apis/timeline-cache'
import { holdsEachWordOf, joinWords, mentionedPersonIds, mergeWords, plainPreviewOf } from '../editor/content'
import { pointAtCreatedSession, pointAtNewEntry } from './composer-pointer'
import { prefixOfOutbox, readOutbox, removeFromOutbox, storedEntriesIn, writeToOutbox, type OutboxEntry, type StoredEntry } from './outbox'
import { queueWithRefusal, queueWithUnanswered, type SaveStatus, type Stamped } from './save-queue'
import { isARowThatIsGone, isFinalRefusal, SerialSaver } from './serial-saver'
import { hasWriterLeft, isKeyStored, takeKeysOfTabsThatLeft, thisTab, type OtherTabReport } from './tab-keys'

export type DraftConflict = 'wordsMovedToHistory' | 'discardedInAnotherTab' | null

export type ShownSaveStatus = SaveStatus | { kind: 'savedInHistory'; at: string }

export interface DraftSnapshot {
  status: SaveStatus
  conflict: DraftConflict
  sessionId: string | null
  baseRevision: number
  movedFromDeletedEntry: boolean
  isOnlyInMemory: boolean
  areWordsWithAnotherTab: boolean
}

export type DraftNotice =
  | { kind: 'moved'; saver: DraftSaver; sessionId: string | null }
  | { kind: 'joined'; saver: DraftSaver; sessionId: string | null }
  | { kind: 'inHistory'; saver: DraftSaver; sessionId: string | null; words: string }
  | { kind: 'refused'; saver: DraftSaver; reason: string; words: string }
  | { kind: 'waiting'; saver: DraftSaver; words: string }
  | { kind: 'onlyInMemory'; saver: DraftSaver; reason: string | null; canDiscard: boolean; words: string }

export function shownStatusOf({ status, conflict, areWordsWithAnotherTab }: DraftSnapshot): ShownSaveStatus {
  if (status.kind !== 'saved') return status
  if (areWordsWithAnotherTab) return { kind: 'idle' }
  if (conflict === null) return status
  return conflict === 'wordsMovedToHistory' ? { kind: 'savedInHistory', at: status.at } : { kind: 'idle' }
}

export type WhyNewWords = 'heldWordsJoined' | 'draftHoldsMore'

/** Where the words of a save are, by the text of the Draft. A word of these words that is not in the Draft needs the person. */
export type WordsFate = 'areTheDraft' | 'areInTheDraft' | 'onlyInHistory'

export function fateOfWords(words: string, draft: string): WordsFate {
  if (words === draft) return 'areTheDraft'
  return holdsEachWordOf(draft, words) ? 'areInTheDraft' : 'onlyInHistory'
}

/** `reply` is null when no save went, because another tab took or discarded the key. `newest` is the read that a save needs after `Kept` or `Lost`. */
interface SaveAnswer {
  reply: DraftSaveResultDto | null
  newest: SessionDto | null
  isKeyWithAnotherTab?: boolean
}

const doesKeyHoldWords = ({ change, wordsInHistory }: OutboxEntry, words: string) => change?.value === words || wordsInHistory === words

export interface DraftTarget {
  owner: string
  channel: DraftChannel
  clientSessionId: string
  startedAt: string
  sessionId: string | null
  baseRevision: number
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

  constructor(target: DraftTarget, queryClient: QueryClient, resumed?: StoredEntry | CopyOfAKey) {
    const entry = resumed?.entry
    this.target = target
    this.queryClient = queryClient
    this.snapshot = {
      status: { kind: 'idle' },
      conflict: null,
      sessionId: target.sessionId,
      baseRevision: target.baseRevision,
      movedFromDeletedEntry: entry?.movedFromDeletedEntry ?? false,
      isOnlyInMemory: false,
      areWordsWithAnotherTab: false,
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
    this.reportsArrival = this.heldWords !== null || (entry.change !== null && !entry.refusal)
    if (entry.wordsInHistory) this.noteWordsOnlyInHistory(entry.wordsInHistory)
    if (this.heldWords !== null) queueMicrotask(() => this.holdRun === 0 && void this.joinHoldWithNoPage())
  }

  /** A page can open before this tab can know that another tab left. That page shows the text of the tab if the person has done nothing on the page. */
  takeEntryIfUntouched(stored: StoredEntry) {
    const { entry } = stored
    // A key that holds only a notice names words that are not on this page. A saver of its own shows them.
    if (this.hasBeenTouched || this.isStopped || entry.refusal || (entry.change === null && entry.wordsInHistory)) return false
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
      if (session && unsettled === null) this.moveBaseTo(session.rawDraftRevision)
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

  /** The words of the person join the newest Draft. The answer is the words to show, and null means that nothing changed. A page that opened before the notice came does not show the words in History, so they join too. */
  async keepWords(text: string): Promise<string | null> {
    if (this.snapshot.conflict === null) return null
    // With no entry yet there is no Draft to read. The create and the rule of the base decide.
    if (this.snapshot.sessionId === null) {
      this.dismissConflict()
      this.edit(text)
      this.saver.flush()
      return text
    }
    const newest = await this.readNewestDraft()
    if (newest === null) return null
    const history = this.wordsInHistory
    const mine = history === null || holdsEachWordOf(text, history) ? text : mergeWords(text, history)
    const merged = mergeWords(newest, mine)
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
    this.moveBaseTo(this.isRaw ? session.rawDraftRevision : session.cleanedDraftRevision)
  }

  private draftTextIn(session: SessionDto) {
    return this.isRaw ? session.rawText : (session.cleanedText ?? '')
  }

  moveBaseTo(draftRevision: number) {
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
    const reply = await this.saveWords(change, signal)
    return { reply, newest: reply.outcome === 'Kept' || reply.outcome === 'Lost' ? await this.readForAnswer(signal) : null }
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
      return await this.readSessionNow(id)
    } catch (error) {
      // A refused read is no refusal of the words, so the save goes again. A read of an entry that is gone moves the words.
      throw isFinalRefusal(error) && !isARowThatIsGone(error) ? new Error('The read failed.') : error
    } finally {
      signal.removeEventListener('abort', stopRead)
    }
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
    this.update({ sessionId: null, baseRevision: 0, conflict: null, movedFromDeletedEntry: true })
    this.keep(change)
  }

  private handleReply({ reply, newest, isKeyWithAnotherTab }: SaveAnswer, change: Stamped<string>) {
    this.replyCount += 1
    // With no reply, each later save of this queue also comes from the key that is gone.
    if (reply) this.forgetAKeyThatIsGone()
    const id = this.snapshot.sessionId
    if (id && reply) void this.queryClient.invalidateQueries({ queryKey: SessionKeys.revisions(id, this.target.channel) })
    if (newest && id && this.isRaw) this.patchRow(id, newest.rawText, newest)
    if (reply && !newest) {
      this.noteWordsAreTheDraft(change.value, reply.draftRevision ?? this.snapshot.baseRevision)
      if (id) this.keepSessionCurrent(id, reply, change.value)
      return
    }
    const fate = newest ? fateOfWords(change.value, this.draftTextIn(newest)) : 'onlyInHistory'
    if (fate === 'areTheDraft' && newest) this.noteWordsAreTheDraft(change.value, this.isRaw ? newest.rawDraftRevision : newest.cleanedDraftRevision)
    else if (fate === 'areInTheDraft' && newest) this.noteDraftHoldsMoreThan(change.value, newest)
    else if (reply) this.noteWordsOnlyInHistory(change.value)
    else if (isKeyWithAnotherTab) this.update({ areWordsWithAnotherTab: true })
    else if (this.listeners.size > 0) this.update({ conflict: 'discardedInAnotherTab' })
  }

  /** Only a read or a save that holds these words moves the base, so a later save cannot push a chunk of a recording out of the Draft. */
  private noteWordsAreTheDraft(words: string, draftRevision: number) {
    this.wordsOnServer = words
    this.wordsInHistory = null
    this.noteArrival()
    this.update({ baseRevision: draftRevision, conflict: null, areWordsWithAnotherTab: false })
  }

  /** The words of this save are safe, and the Draft holds more. The page takes the Draft, unless the person typed since. Then the next save decides. */
  private noteDraftHoldsMoreThan(words: string, newest: SessionDto) {
    this.noteArrival()
    if (this.unsettledText() !== words) return
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
