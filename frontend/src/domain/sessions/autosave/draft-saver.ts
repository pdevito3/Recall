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
import { joinWords, mentionedPersonIds, mergeWords, plainPreviewOf } from '../editor/content'
import { pointAtCreatedSession, pointAtNewEntry } from './composer-pointer'
import { prefixOfOutbox, readOutbox, removeFromOutbox, storedEntriesIn, writeToOutbox, type OutboxEntry, type StoredEntry } from './outbox'
import { queueWithRefusal, queueWithUnanswered, type SaveStatus, type Stamped } from './save-queue'
import { isARowThatIsGone, SerialSaver } from './serial-saver'
import { hasWriterLeft, takeKeysOfTabsThatLeft, thisTab, type OtherTabReport } from './tab-keys'

export type DraftConflict = 'wordsMovedToHistory' | 'replacedUnseenSave' | null

export type ShownSaveStatus = SaveStatus | { kind: 'savedInHistory'; at: string }

export interface DraftSnapshot {
  status: SaveStatus
  conflict: DraftConflict
  sessionId: string | null
  baseRevision: number
  latestRevision: number
  movedFromDeletedEntry: boolean
  isOnlyInMemory: boolean
}

export type DraftNotice =
  | { kind: 'moved'; saver: DraftSaver; sessionId: string | null }
  | { kind: 'joined'; saver: DraftSaver; sessionId: string | null }
  | { kind: 'inHistory'; saver: DraftSaver; sessionId: string | null }
  | { kind: 'refused'; saver: DraftSaver; reason: string; words: string }
  | { kind: 'waiting'; saver: DraftSaver; words: string }
  | { kind: 'onlyInMemory'; saver: DraftSaver; reason: string | null; canDiscard: boolean; words: string }

export function shownStatusOf({ status, conflict }: DraftSnapshot): ShownSaveStatus {
  return status.kind === 'saved' && conflict === 'wordsMovedToHistory' ? { kind: 'savedInHistory', at: status.at } : status
}

export interface DraftTarget {
  owner: string
  channel: DraftChannel
  clientSessionId: string
  startedAt: string
  sessionId: string | null
  baseRevision: number
  latestRevision: number
  location?: Promise<{ latitude: number; longitude: number } | null>
}

const pauseInTypingMs = 800
const holdReadLimitMs = 30_000
const holdReadRetryMs = 2_000
const previewLength = 200

export class DraftSaver {
  private target: DraftTarget
  private readonly queryClient: QueryClient
  private readonly saver: SerialSaver<string, DraftSaveResultDto>
  private snapshot: DraftSnapshot
  private readonly listeners = new Set<() => void>()
  private replyCount = 0
  private conflictIsUnseen = false
  private wordsInHistory: string | null = null
  private wordsOnServer: string | null = null
  private heldWords: string | null = null
  private ownKey: string | null = null
  private hasBeenTouched = false
  private hasUnseenJoin = false
  private holdRun = 0
  private readonly holdJoinListeners = new Set<(words: string) => void>()
  private isStopped = false
  private isDiscarded = false
  private isBeingDeleted = false

  constructor(target: DraftTarget, queryClient: QueryClient, resumed?: StoredEntry) {
    const entry = resumed?.entry
    this.target = target
    this.queryClient = queryClient
    this.snapshot = {
      status: { kind: 'idle' },
      conflict: null,
      sessionId: target.sessionId,
      baseRevision: target.baseRevision,
      latestRevision: target.latestRevision,
      movedFromDeletedEntry: entry?.movedFromDeletedEntry ?? false,
      isOnlyInMemory: false,
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
  private takeKeyOf({ storageKey, entry }: StoredEntry) {
    this.hasBeenTouched = true
    this.ownKey = storageKey
    this.heldWords = entry.held ?? null
    if (this.heldWords !== null) queueMicrotask(() => this.holdRun === 0 && void this.joinHoldWithNoPage())
  }

  /** A page can open before this tab can know that another tab left. That page shows the text of the tab if the person has done nothing on the page. */
  takeEntryIfUntouched(stored: StoredEntry) {
    const { entry } = stored
    if (this.hasBeenTouched || this.isStopped || entry.refusal) return false
    this.update({ sessionId: entry.sessionId ?? this.snapshot.sessionId, baseRevision: entry.baseRevision, latestRevision: Math.max(entry.latestRevision, this.snapshot.latestRevision) })
    this.takeKeyOf(stored)
    if (entry.change) {
      this.saver.edit(entry.change.value, entry.change.savedAt)
      for (const listener of this.holdJoinListeners) listener(entry.change.value)
    } else this.keep()
    return true
  }

  edit(text: string) {
    this.hasBeenTouched = true
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

  /** Words typed before a read answered go after the words of the entry. One write moves them from the hold to the queue. */
  joinHoldInto(words: string) {
    if (this.heldWords === null) return words
    const joined = joinWords(words, this.heldWords)
    this.heldWords = null
    this.saver.edit(joined)
    return joined
  }

  /** A page that shows this entry opens the joined words at once, and its next key must not save the old text over them. */
  subscribeToHoldJoin(listener: (words: string) => void) {
    this.holdJoinListeners.add(listener)
    return () => {
      this.holdJoinListeners.delete(listener)
    }
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
      if (session && unsettled === null) this.moveBaseTo(session.rawDraftRevision, session.rawLatestRevision)
      const joined = this.joinHoldInto(unsettled ?? session?.rawText ?? '')
      for (const listener of this.holdJoinListeners) listener(joined)
      this.hasUnseenJoin = this.holdJoinListeners.size === 0
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

  keepsTheSameWordsAs({ key, change, held }: OutboxEntry) {
    const own = this.saver.newestUnsettled()
    return this.ownKey !== null && !this.snapshot.isOnlyInMemory && key === draftKey(this.target) && own?.value === change?.value && own?.savedAt === change?.savedAt && this.heldWords === (held ?? null)
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
    if (isOnlyInMemory) return { kind: 'onlyInMemory', saver: this, reason: status.kind === 'refused' ? status.message : null, canDiscard: hasNoPage, words }
    if (status.kind === 'refused' && hasNoPage) return { kind: 'refused', saver: this, reason: status.message, words }
    if (status.kind === 'waiting' && hasNoPage) return { kind: 'waiting', saver: this, words }
    if (movedFromDeletedEntry) return { kind: 'moved', saver: this, sessionId }
    if (hasNoPage && this.hasUnseenConflict() && conflict === 'wordsMovedToHistory') return { kind: 'inHistory', saver: this, sessionId }
    return hasNoPage && this.hasUnseenJoin ? { kind: 'joined', saver: this, sessionId } : null
  }

  unsettledText() {
    return this.saver.newestUnsettledValue()
  }

  /** A hold can have joined the entry after Done, so these words can be newer than the words at Done. */
  newestWords() {
    return this.unsettledText() ?? (this.snapshot.conflict === 'wordsMovedToHistory' ? this.wordsInHistory : this.wordsOnServer)
  }

  /** The words a page must open with: those not yet settled, or those that lost a save while no page showed them. */
  wordsToOpen() {
    const hasUnseenLoss = this.conflictIsUnseen && this.snapshot.conflict === 'wordsMovedToHistory'
    const kept = this.unsettledText() ?? (hasUnseenLoss ? this.wordsInHistory : null)
    return kept === null ? null : this.joinHoldInto(kept)
  }

  isHoldingWordsOnlyInMemory() {
    return this.snapshot.isOnlyInMemory && !this.isStopped
  }

  lastSavedWords() {
    return this.wordsOnServer
  }

  hasUnseenConflict() {
    return this.conflictIsUnseen && this.snapshot.conflict !== null
  }

  dismissConflict() {
    this.update({ conflict: null })
  }

  /** A save with no read can push a chunk of a recording out of the Draft, so the words of the person join the newest Draft. The answer is the words to show, and null means that nothing changed. */
  async keepWords(text: string): Promise<string | null> {
    if (this.snapshot.conflict !== 'wordsMovedToHistory') {
      this.dismissConflict()
      return null
    }
    const newest = await this.readNewestDraft()
    if (newest === null) return null
    const merged = mergeWords(newest, text)
    this.dismissConflict()
    this.edit(merged)
    this.saver.flush()
    return merged
  }

  async takeNewestWords(): Promise<string | null> {
    if (this.unsettledText() !== null) return null
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
    const newest = await this.queryClient.fetchQuery({ ...sessionQueryOptions(id), staleTime: 0 }).catch((error: unknown) => {
      if (!isCancelledError(error)) Notification.error('The newest words did not load. Try again.')
      return null
    })
    if (!newest || this.unsettledText() !== unsettledBefore || this.replyCount !== repliesBefore) return null
    const isRaw = this.target.channel === 'raw'
    this.moveBaseTo(isRaw ? newest.rawDraftRevision : newest.cleanedDraftRevision, isRaw ? newest.rawLatestRevision : newest.cleanedLatestRevision)
    return isRaw ? newest.rawText : (newest.cleanedText ?? '')
  }

  moveBaseTo(draftRevision: number, latestRevision: number) {
    this.update({ baseRevision: draftRevision, latestRevision: Math.max(latestRevision, this.snapshot.latestRevision) })
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
    sendingForATabThatLeft.delete(this)
    refreshDraftNotices()
  }

  subscribe = (listener: () => void) => {
    this.listeners.add(listener)
    this.conflictIsUnseen = false
    refreshDraftNotices()
    return () => {
      this.listeners.delete(listener)
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
    const location = await this.target.location
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
    // A replay of the create can bring words that this page never showed, also a chunk of a recording. The base stays, so a next save over them raises the notice.
    this.update({ sessionId: created.id })
    this.queryClient.setQueryData(SessionKeys.detail(created.id), created)
    void refreshTimelineHead(this.queryClient)
  }

  private async send(change: Stamped<string>, signal: AbortSignal) {
    try {
      return await this.saveWords(change, signal)
    } catch (error) {
      // The delete of this tab, or a discard at logout, is the wish of the person. The words must not come back as a new entry.
      if (!isARowThatIsGone(error) || this.isStopped || this.isBeingDeleted) throw error
      this.moveToNewEntry(change)
      return this.saveWords(change, signal)
    }
  }

  private async saveWords(change: Stamped<string>, signal: AbortSignal) {
    const id = await this.sessionIdForSave(signal)
    const body = { text: change.value, baseRevision: this.snapshot.baseRevision, savedAt: change.savedAt }
    const options = { suppressErrorToast: true }
    return this.target.channel === 'raw'
      ? saveRawDraft({ path: { id }, body, signal, options })
      : saveCleanedDraft({ path: { id }, body, signal, options })
  }

  /** A failed write leaves the older key, which still holds the words. */
  private moveToNewEntry(change: Stamped<string>) {
    const clientSessionId = newUuid()
    pointAtNewEntry(this.target.clientSessionId, clientSessionId)
    this.target = { ...this.target, clientSessionId, channel: 'raw', sessionId: null, baseRevision: 0, latestRevision: 0 }
    savers.set(draftKey(this.target), this)
    this.update({ sessionId: null, baseRevision: 0, latestRevision: 0, conflict: null, movedFromDeletedEntry: true })
    this.keep(change)
  }

  private handleReply(reply: DraftSaveResultDto, change: Stamped<string>) {
    this.replyCount += 1
    const { baseRevision, latestRevision, conflict } = this.snapshot
    const holdsWords = draftHoldsTheseWords(reply, change, baseRevision, latestRevision)
    // The base stays behind after a loss, so a next save cannot push a chunk of a recording out of the Draft. Only a read moves it.
    if (holdsWords) this.update({ baseRevision: reply.draftRevision })
    if (holdsWords || reply.outcome === 'Lost') this.update({ latestRevision: Math.max(latestRevision, reply.latestRevision) })
    const nextConflict = conflictAfter(reply, holdsWords, latestRevision, conflict)
    if (nextConflict !== conflict || reply.outcome === 'Lost') this.conflictIsUnseen = this.listeners.size === 0
    if (reply.outcome === 'Lost') this.wordsInHistory = change.value
    if (holdsWords) this.wordsOnServer = change.value
    this.update({ conflict: nextConflict })
    const id = this.snapshot.sessionId
    if (!id) return
    this.keepSessionCurrent(id, reply, holdsWords ? change.value : null)
    void this.queryClient.invalidateQueries({ queryKey: SessionKeys.revisions(id, this.target.channel) })
    if (!holdsWords) void this.readNewest(id)
  }

  private keepSessionCurrent(id: string, reply: DraftSaveResultDto, words: string | null) {
    const key = SessionKeys.detail(id)
    const isLoaded = this.queryClient.getQueryData(key) !== undefined
    void this.queryClient.cancelQueries({ queryKey: key, exact: true }).then(() => {
      if (!isLoaded) void this.queryClient.invalidateQueries({ queryKey: key, exact: true })
    })
    this.queryClient.setQueryData<SessionDto>(key, (session) => {
      if (!session) return session
      const isRaw = this.target.channel === 'raw'
      const latest = isRaw ? { rawLatestRevision: reply.latestRevision } : { cleanedLatestRevision: reply.latestRevision }
      const draft = words === null
        ? {}
        : isRaw
          ? { rawDraftRevision: reply.draftRevision, rawText: words, rawSavedAt: reply.savedAt, personIds: mentionedPersonIds(words, session.cleanedText) }
          : { cleanedDraftRevision: reply.draftRevision, cleanedText: words, cleanedSavedAt: reply.savedAt, personIds: mentionedPersonIds(session.rawText, words) }
      return { ...session, ...latest, ...draft, cleanupStatus: reply.cleanupStatus, version: reply.version }
    })
    if (words !== null && this.target.channel === 'raw') this.patchRow(id, words, reply)
  }

  private async readNewest(id: string) {
    const newest = await this.queryClient.fetchQuery({ ...sessionQueryOptions(id), staleTime: 0 }).catch(() => null)
    if (newest && this.target.channel === 'raw') this.patchRow(id, newest.rawText, newest)
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
    if (this.isDiscarded) return
    const change = this.saver.newestUnsettled() ?? fallback
    if (change === null && this.heldWords === null) {
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
      latestRevision: this.snapshot.latestRevision,
      change,
      held: this.heldWords,
      refusal,
      movedFromDeletedEntry: this.snapshot.movedFromDeletedEntry,
    }
  }
}

export function draftHoldsTheseWords(reply: DraftSaveResultDto, change: Stamped<string>, baseRevision: number, latestRevision: number): boolean {
  if (reply.outcome !== 'Unchanged') return reply.outcome === 'Won'
  const isDraftThisPageKnows = reply.draftRevision === baseRevision
  const nextRevision = latestRevision + 1
  // A chunk of a recording is also one new revision, and a chunk has its own time.
  const isThisSaveWithNoReply = reply.draftRevision === nextRevision && reply.latestRevision === nextRevision && Date.parse(reply.savedAt ?? '') === Date.parse(change.savedAt)
  return isDraftThisPageKnows || isThisSaveWithNoReply
}

function conflictAfter(reply: DraftSaveResultDto, holdsWords: boolean, latestRevision: number, current: DraftConflict): DraftConflict {
  if (reply.outcome === 'Lost') return 'wordsMovedToHistory'
  const wasOnlyInHistory = current === 'wordsMovedToHistory'
  if (reply.outcome === 'Won' && (wasOnlyInHistory || reply.draftRevision > latestRevision + 1)) return 'replacedUnseenSave'
  return holdsWords && wasOnlyInHistory ? null : current
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
  const [newest, ...older] = readOutbox(target.owner)
    .filter(({ writer, entry }) => entry.key === key && writer !== thisTab && hasWriterLeft(writer))
    .sort(newestFirst)
  const saver = newest
    ? new DraftSaver({ ...target, sessionId: newest.entry.sessionId, baseRevision: newest.entry.baseRevision, latestRevision: newest.entry.latestRevision }, queryClient, newest)
    : new DraftSaver(target, queryClient)
  savers.set(key, saver)
  for (const stored of older) takeEntry(target.owner, stored, queryClient)
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

const everySaver = () => [...new Set(savers.values()), ...sendingForATabThatLeft]

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

export const wordsOfThisTabOnlyInMemory = () =>
  everySaver().flatMap((saver) => (saver.isHoldingWordsOnlyInMemory() ? (saver.unsentWords() ?? []) : []))

export interface UnsentWords {
  words: string
  isRefused: boolean
  isOnlyInMemory: boolean
  discard: () => void
}

/** All the words that a sign-out leaves on this device or loses. A discard removes the exact keys of this list, so it removes no word that the list did not show. */
export function unsentWordsOnThisDevice(owner: string | null, otherTabs: OtherTabReport[]): UnsentWords[] {
  const ofThisTab = everySaver().flatMap((saver) => {
    const words = saver.unsentWords()
    return words === null ? [] : { words, isRefused: saver.getSnapshot().status.kind === 'refused', isOnlyInMemory: saver.isHoldingWordsOnlyInMemory(), discard: () => saver.discard() }
  })
  const inKeysOfOtherTabs = (owner === null ? [] : readOutbox(owner)).filter(({ writer }) => writer !== thisTab).map(({ storageKey, entry }) => ({
    words: [entry.change?.value ?? null, entry.held ?? null].filter((text) => text !== null).reduce(joinWords),
    isRefused: Boolean(entry.refusal),
    isOnlyInMemory: false,
    discard: () => removeFromOutbox(storageKey),
  }))
  const inMemoryOfOtherTabs = otherTabs.flatMap(({ wordsOnlyInMemory }) => wordsOnlyInMemory.map((words) => ({ words, isRefused: false, isOnlyInMemory: true, discard: () => undefined })))
  return [...ofThisTab, ...inKeysOfOtherTabs, ...inMemoryOfOtherTabs]
}
