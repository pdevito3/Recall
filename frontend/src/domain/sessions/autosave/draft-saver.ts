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
import { joinWords, mentionedPersonIds, plainPreviewOf, plainWordsOf } from '../editor/content'
import { pointAtCreatedSession, pointAtNewEntry } from './composer-pointer'
import { claimHold } from './hold-lock'
import { readHoldsFromOutbox, readOutbox, readOutboxEntry, removeFromOutbox, removeHoldsFromOutbox, writeHoldToOutbox, writeToOutbox, type HeldSlot, type OutboxEntry } from './outbox'
import { queueWithRefusal, queueWithUnanswered, newestUnsettled, type SaveQueue, type SaveStatus, type Stamped } from './save-queue'
import { isARowThatIsGone, SerialSaver } from './serial-saver'

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
  private heldTexts: string[] = []
  private readonly holdSlot = newUuid()
  private joinedSlots: HeldSlot[] | null = null
  private readonly isOnlyInMemory = { queue: false, hold: false }
  private holdRun = 0
  private releaseHold: (() => void) | null = null
  private ownsHold = false
  private isHoldReadByPage = false
  private readonly holdJoinListeners = new Set<(words: string) => void>()
  private isStopped = false
  private isDiscarded = false
  private isBeingDeleted = false

  constructor(target: DraftTarget, queryClient: QueryClient, resumed?: OutboxEntry | null, resumedHolds: OutboxEntry[] = []) {
    this.target = target
    this.queryClient = queryClient
    this.snapshot = {
      status: { kind: 'idle' },
      conflict: null,
      sessionId: target.sessionId,
      baseRevision: target.baseRevision,
      latestRevision: target.latestRevision,
      movedFromDeletedEntry: resumed?.movedFromDeletedEntry ?? false,
      isOnlyInMemory: false,
    }
    this.saver = new SerialSaver({
      debounceMs: pauseInTypingMs,
      send: (change, signal) => this.send(change, signal),
      onReply: (reply, change) => this.handleReply(reply, change),
      onQueueChange: (queue) => this.keepInOutbox(queue),
      initialQueue: resumed ? (resumed.refusal ? queueWithRefusal(resumed.change, resumed.refusal) : queueWithUnanswered(resumed.change)) : undefined,
    })
    this.snapshot = { ...this.snapshot, status: this.saver.getStatus() }
    this.saver.subscribe(() => this.update({ status: this.saver.getStatus() }))
    if (resumedHolds.length > 0) {
      this.heldTexts = resumedHolds.map(({ change }) => change.value)
      this.heldWords = wordsOfEachHold(this.heldTexts)
      this.claimTheHold()
    }
  }

  edit(text: string) {
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
    this.letGoOfHold()
  }

  noteDelete(isRunning: boolean) {
    this.isBeingDeleted = isRunning
  }

  wordsInHold() {
    return this.heldWords
  }

  holdInOutbox(text: string) {
    this.heldWords = text
    this.claimTheHold()
    const entry = this.outboxEntry({ value: text, savedAt: new Date().toISOString() }, null)
    this.noteOutboxWrite('hold', writeHoldToOutbox(this.target.owner, entry, this.holdSlot))
  }

  releaseFromOutbox() {
    this.heldWords = null
    removeHoldsFromOutbox(this.target.owner, draftKey(this.target), [this.holdSlot])
    this.letGoOfHold()
    this.noteOutboxWrite('hold', true)
  }

  /** A page that reads the entry joins the held words itself, so the read of the saver stops. */
  takeHold() {
    this.holdRun += 1
    this.isHoldReadByPage = true
  }

  /** Words typed before a read answered go after the words of the entry. Only the tab that has the lock joins them, with the slot of each tab, and the words stay in the hold until then. */
  joinHoldInto(words: string) {
    if (this.heldWords === null || !this.ownsHold) return words
    const held = this.wordsHeldNow()
    if (held.words === null) {
      this.heldWords = null
      this.letGoOfHold()
      return words
    }
    const joined = joinWords(words, held.words)
    this.saveJoined(joined, held.slots)
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
    this.isHoldReadByPage = false
    if (!this.ownsHold) return
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
    }
  }

  private claimTheHold() {
    this.releaseHold ??= claimHold(`${this.target.owner}.${draftKey(this.target)}`, () => {
      if (this.isStopped) return
      this.ownsHold = true
      // Another tab can have joined the words while this tab waited, or typed more of them.
      const held = this.wordsHeldNow()
      this.heldWords = held.words
      this.heldTexts = held.slots.map(({ entry }) => entry.change.value)
      if (this.heldWords === null) this.letGoOfHold()
      else if (!this.isHoldReadByPage) void this.joinHoldWithNoPage()
    })
  }

  private letGoOfHold() {
    this.releaseHold?.()
    this.releaseHold = null
    this.ownsHold = false
  }

  /** The slots hold every word that another tab typed. This tab adds its own words when the browser had no room for them. */
  private wordsHeldNow() {
    const held = readHoldsFromOutbox(this.target.owner, draftKey(this.target))
    const own = this.isOnlyInMemory.hold && this.heldWords !== null ? [this.heldWords] : []
    const texts = [...own, ...held.map(({ entry }) => entry.change.value)]
    return { words: texts.length > 0 ? wordsOfEachHold(texts) : null, slots: held }
  }

  /** The joined words go out of the hold after they are in the queue of the outbox, so a close between the two can show the words twice and cannot lose them. */
  private saveJoined(joined: string, slots: HeldSlot[]) {
    this.saver.edit(joined)
    this.heldWords = null
    this.noteOutboxWrite('hold', true)
    this.joinedSlots = slots
    this.removeJoinedHolds()
  }

  private removeJoinedHolds() {
    if (this.joinedSlots === null || this.isOnlyInMemory.queue) return
    // A tab can have typed more in its slot since the join, and those words are not in the queue.
    const joined = this.joinedSlots
    const unchanged = readHoldsFromOutbox(this.target.owner, draftKey(this.target)).filter(({ slot, entry }) =>
      joined.some((old) => old.slot === slot && old.entry.change.value === entry.change.value),
    )
    removeHoldsFromOutbox(this.target.owner, draftKey(this.target), unchanged.map(({ slot }) => slot))
    this.joinedSlots = null
    this.letGoOfHold()
  }

  dismissMovedNotice() {
    this.update({ movedFromDeletedEntry: false })
    const change = this.saver.newestUnsettled()
    if (change) this.writeToOutbox(change, this.saver.refusalMessage())
  }

  notice(): DraftNotice | null {
    const { status, movedFromDeletedEntry, sessionId, isOnlyInMemory } = this.snapshot
    const unsent = [this.unsettledText(), this.heldWords].filter((text) => text !== null)
    const words = unsent.length > 0 ? wordsOfEachHold(unsent) : ''
    // An open page keeps its saver and its editor, so a discard there would leave a page that saves nothing.
    if (isOnlyInMemory) return { kind: 'onlyInMemory', saver: this, reason: status.kind === 'refused' ? status.message : null, canDiscard: this.listeners.size === 0, words }
    if (status.kind === 'refused' && this.listeners.size === 0) {
      return { kind: 'refused', saver: this, reason: status.message, words }
    }
    if (status.kind === 'waiting' && this.listeners.size === 0) return { kind: 'waiting', saver: this, words }
    return movedFromDeletedEntry ? { kind: 'moved', saver: this, sessionId } : null
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

  keepWords(text: string) {
    const isOnlyInHistory = this.snapshot.conflict === 'wordsMovedToHistory'
    this.dismissConflict()
    if (!isOnlyInHistory) return
    this.saver.edit(text)
    this.saver.flush()
  }

  async takeNewestWords(): Promise<string | null> {
    const id = this.snapshot.sessionId
    if (!id) return null
    const repliesBefore = this.replyCount
    const newest = await this.queryClient.fetchQuery({ ...sessionQueryOptions(id), staleTime: 0 }).catch((error: unknown) => {
      if (!isCancelledError(error)) Notification.error('The newest words did not load. Try again.')
      return null
    })
    if (!newest || this.unsettledText() !== null || this.replyCount !== repliesBefore) return null
    const isRaw = this.target.channel === 'raw'
    this.moveBaseTo(isRaw ? newest.rawDraftRevision : newest.cleanedDraftRevision, isRaw ? newest.rawLatestRevision : newest.cleanedLatestRevision)
    this.dismissConflict()
    return isRaw ? newest.rawText : (newest.cleanedText ?? '')
  }

  moveBaseTo(draftRevision: number, latestRevision: number) {
    this.update({ baseRevision: draftRevision, latestRevision: Math.max(latestRevision, this.snapshot.latestRevision) })
  }

  discard() {
    this.isDiscarded = true
    this.stop()
    removeFromOutbox(this.target.owner, draftKey(this.target))
    this.removeHoldsThisTabKnows()
    for (const [key, saver] of savers) {
      if (saver === this) savers.delete(key)
    }
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
    // A replay of the create can bring words that this page never showed. A next save over them must raise the notice.
    this.update({ sessionId: created.id, baseRevision: created.rawDraftRevision })
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

  private moveToNewEntry(change: Stamped<string>) {
    const oldKey = draftKey(this.target)
    const clientSessionId = newUuid()
    pointAtNewEntry(this.target.clientSessionId, clientSessionId)
    this.target = { ...this.target, clientSessionId, channel: 'raw', sessionId: null, baseRevision: 0, latestRevision: 0 }
    savers.set(draftKey(this.target), this)
    this.update({ sessionId: null, baseRevision: 0, latestRevision: 0, conflict: null, movedFromDeletedEntry: true })
    this.writeToOutbox(this.saver.newestUnsettled() ?? change, null)
    // With no room for the new copy, the old copy is the only one that a crash leaves. A later load can then make a second entry, and no word goes.
    if (!this.isOnlyInMemory.queue) removeFromOutbox(this.target.owner, oldKey)
    const heldInOldEntry = readHoldsFromOutbox(this.target.owner, oldKey).map(({ entry }) => entry.change.value)
    const held = [...(this.heldWords === null ? [] : [this.heldWords]), ...heldInOldEntry]
    if (held.length > 0) {
      this.joinedSlots = null
      this.letGoOfHold()
      this.holdInOutbox(wordsOfEachHold(held))
    }
    if (!this.isOnlyInMemory.hold) removeHoldsFromOutbox(this.target.owner, oldKey, readHoldsFromOutbox(this.target.owner, oldKey).map(({ slot }) => slot))
  }

  /** A discard covers the exact texts that the person saw in this tab. A slot with other words belongs to a tab that still writes, and stays. */
  private removeHoldsThisTabKnows() {
    const known = [...(this.heldWords === null ? [] : this.heldTexts), ...(this.joinedSlots ?? []).map(({ entry }) => entry.change.value)]
    const key = draftKey(this.target)
    const slots = readHoldsFromOutbox(this.target.owner, key).filter(({ slot, entry }) => slot === this.holdSlot || known.includes(entry.change.value))
    removeHoldsFromOutbox(this.target.owner, key, slots.map(({ slot }) => slot))
  }

  private handleReply(reply: DraftSaveResultDto, change: Stamped<string>) {
    this.replyCount += 1
    const { baseRevision, latestRevision, conflict } = this.snapshot
    const holdsWords = draftHoldsTheseWords(reply, baseRevision, latestRevision)
    if (holdsWords || reply.outcome === 'Lost') {
      this.update({ baseRevision: reply.draftRevision, latestRevision: Math.max(latestRevision, reply.latestRevision) })
    }
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

  private keepInOutbox(queue: SaveQueue<string>) {
    const change = newestUnsettled(queue)
    if (!change) {
      removeFromOutbox(this.target.owner, draftKey(this.target))
      this.noteOutboxWrite('queue', true)
      return
    }
    this.writeToOutbox(change, queue.refusal?.change === change ? queue.refusal.message : null)
  }

  private writeToOutbox(change: Stamped<string>, refusal: string | null) {
    if (!this.isDiscarded) this.noteOutboxWrite('queue', writeToOutbox(this.target.owner, this.outboxEntry(change, refusal)))
  }

  private noteOutboxWrite(store: 'queue' | 'hold', isWritten: boolean) {
    this.isOnlyInMemory[store] = !isWritten
    const isOnlyInMemory = this.isOnlyInMemory.queue || this.isOnlyInMemory.hold
    if (this.snapshot.isOnlyInMemory !== isOnlyInMemory) this.update({ isOnlyInMemory })
    else if (isOnlyInMemory) refreshDraftNotices()
    if (store === 'queue' && isWritten) this.removeJoinedHolds()
  }

  private outboxEntry(change: Stamped<string>, refusal: string | null): OutboxEntry {
    return {
      key: draftKey(this.target),
      channel: this.target.channel,
      clientSessionId: this.target.clientSessionId,
      startedAt: this.target.startedAt,
      sessionId: this.snapshot.sessionId,
      baseRevision: this.snapshot.baseRevision,
      latestRevision: this.snapshot.latestRevision,
      change,
      refusal,
      movedFromDeletedEntry: this.snapshot.movedFromDeletedEntry,
    }
  }
}

export function draftHoldsTheseWords(reply: DraftSaveResultDto, baseRevision: number, latestRevision: number): boolean {
  if (reply.outcome !== 'Unchanged') return reply.outcome === 'Won'
  const isDraftThisPageKnows = reply.draftRevision === baseRevision
  const nextRevision = latestRevision + 1
  const isTheOnlyNewRevision = reply.draftRevision === nextRevision && reply.latestRevision === nextRevision
  return isDraftThisPageKnows || isTheOnlyNewRevision
}

function conflictAfter(reply: DraftSaveResultDto, holdsWords: boolean, latestRevision: number, current: DraftConflict): DraftConflict {
  if (reply.outcome === 'Lost') return 'wordsMovedToHistory'
  const wasOnlyInHistory = current === 'wordsMovedToHistory'
  if (reply.outcome === 'Won' && (wasOnlyInHistory || reply.draftRevision > latestRevision + 1)) return 'replacedUnseenSave'
  return holdsWords && wasOnlyInHistory ? null : current
}

/** A tab writes the whole hold again with each key, so a text that another text starts with adds no words. */
function wordsOfEachHold(texts: string[]) {
  const plain = texts.map(plainWordsOf)
  return texts
    .filter((_, index) => !plain.some((other, otherIndex) => otherIndex !== index && other.startsWith(plain[index]) && (other !== plain[index] || otherIndex < index)))
    .reduce(joinWords)
}

const savers = new Map<string, DraftSaver>()

const draftKey = ({ clientSessionId, channel }: Pick<DraftTarget, 'clientSessionId' | 'channel'>) =>
  `${clientSessionId}.${channel}`

function saverFromOutbox(target: DraftTarget, queryClient: QueryClient) {
  const key = draftKey(target)
  const entry = readOutboxEntry(target.owner, key)
  const holds = readHoldsFromOutbox(target.owner, key).map((held) => held.entry)
  const kept = entry ?? holds[0]
  if (!kept) return new DraftSaver(target, queryClient)
  return new DraftSaver({ ...target, sessionId: kept.sessionId, baseRevision: kept.baseRevision, latestRevision: kept.latestRevision }, queryClient, entry, holds)
}

export function draftSaverFor(target: DraftTarget, queryClient: QueryClient): DraftSaver {
  const key = draftKey(target)
  const existing = savers.get(key)
  if (existing) return existing
  const saver = saverFromOutbox(target, queryClient)
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

export function resumeUnsentDrafts(owner: string, queryClient: QueryClient) {
  for (const entry of readOutbox(owner)) {
    if (!savers.has(entry.key)) savers.set(entry.key, saverFromOutbox({ ...entry, owner }, queryClient))
  }
  refreshDraftNotices()
}

let shownNotices: DraftNotice[] = []
const noticeListeners = new Set<() => void>()

function refreshDraftNotices() {
  shownNotices = [...new Set(savers.values())].flatMap((saver) => saver.notice() ?? [])
  for (const listener of noticeListeners) listener()
}

export const draftNotices = () => shownNotices

export function subscribeToDraftNotices(listener: () => void) {
  noticeListeners.add(listener)
  return () => {
    noticeListeners.delete(listener)
  }
}

const everySaver = () => [...new Set(savers.values())]

export const isAnyWordOnlyInMemory = () => everySaver().some((saver) => saver.isHoldingWordsOnlyInMemory())

export async function settleDraftSavers() {
  const settled = await Promise.all(everySaver().map((saver) => saver.settle()))
  return settled.every(Boolean)
}

export function stopDraftSavers() {
  for (const saver of everySaver()) saver.stop()
}

export function discardUnsentDrafts() {
  for (const saver of everySaver()) saver.discard()
}
