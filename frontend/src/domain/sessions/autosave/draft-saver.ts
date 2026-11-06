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
import { mentionedPersonIds, plainPreviewOf } from '../editor/content'
import { pointAtCreatedSession, pointAtNewEntry } from './composer-pointer'
import { readOutbox, readOutboxEntry, removeFromOutbox, writeToOutbox, type OutboxEntry } from './outbox'
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
}

export type DraftNotice =
  | { kind: 'moved'; saver: DraftSaver; sessionId: string | null }
  | { kind: 'refused'; saver: DraftSaver; reason: string; words: string }

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

  constructor(target: DraftTarget, queryClient: QueryClient, resumed?: OutboxEntry) {
    this.target = target
    this.queryClient = queryClient
    this.snapshot = {
      status: { kind: 'idle' },
      conflict: null,
      sessionId: target.sessionId,
      baseRevision: target.baseRevision,
      latestRevision: target.latestRevision,
      movedFromDeletedEntry: resumed?.movedFromDeletedEntry ?? false,
    }
    this.saver = new SerialSaver({
      debounceMs: pauseInTypingMs,
      send: (change, signal) => this.send(change, signal),
      onReply: (reply, change) => this.handleReply(reply, change),
      onQueueChange: (queue) => this.keepInOutbox(queue),
      initialQueue: resumed && (resumed.refusal ? queueWithRefusal(resumed.change, resumed.refusal) : queueWithUnanswered(resumed.change)),
    })
    this.snapshot = { ...this.snapshot, status: this.saver.getStatus() }
    this.saver.subscribe(() => this.update({ status: this.saver.getStatus() }))
  }

  edit(text: string) {
    this.saver.edit(text)
  }

  flush() {
    this.saver.flush()
  }

  settle() {
    return this.saver.settle()
  }

  stop() {
    this.saver.dispose()
  }

  /** Words typed before the read of the entry ends wait here, so that a reload does not lose them. Nothing goes to the Server. */
  holdInOutbox(text: string | null) {
    if (text === null) removeFromOutbox(this.target.owner, draftKey(this.target))
    else this.writeToOutbox({ value: text, savedAt: new Date().toISOString() }, null)
  }

  dismissMovedNotice() {
    this.update({ movedFromDeletedEntry: false })
    const change = this.saver.newestUnsettled()
    if (change) this.writeToOutbox(change, this.saver.refusalMessage())
  }

  notice(): DraftNotice | null {
    const { status, movedFromDeletedEntry, sessionId } = this.snapshot
    if (status.kind === 'refused' && this.listeners.size === 0) {
      return { kind: 'refused', saver: this, reason: status.message, words: this.unsettledText() ?? '' }
    }
    return movedFromDeletedEntry ? { kind: 'moved', saver: this, sessionId } : null
  }

  unsettledText() {
    return this.saver.newestUnsettledValue()
  }

  /** The words a page must open with: those not yet settled, or those that lost a save while no page showed them. */
  wordsToOpen() {
    const hasUnseenLoss = this.conflictIsUnseen && this.snapshot.conflict === 'wordsMovedToHistory'
    return this.unsettledText() ?? (hasUnseenLoss ? this.wordsInHistory : null)
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
    this.saver.dispose()
    removeFromOutbox(this.target.owner, draftKey(this.target))
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
      if (!isARowThatIsGone(error)) throw error
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

  /** The entry was deleted in another tab or device. The words stay: they become a new entry, and the person gets a notice. */
  private moveToNewEntry(change: Stamped<string>) {
    const oldKey = draftKey(this.target)
    const clientSessionId = newUuid()
    pointAtNewEntry(this.target.clientSessionId, clientSessionId)
    this.target = { ...this.target, clientSessionId, channel: 'raw', sessionId: null, baseRevision: 0, latestRevision: 0 }
    savers.set(draftKey(this.target), this)
    this.update({ sessionId: null, baseRevision: 0, latestRevision: 0, conflict: null, movedFromDeletedEntry: true })
    this.writeToOutbox(change, null)
    removeFromOutbox(this.target.owner, oldKey)
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
      return
    }
    this.writeToOutbox(change, queue.refusal?.change === change ? queue.refusal.message : null)
  }

  private writeToOutbox(change: Stamped<string>, refusal: string | null) {
    writeToOutbox(this.target.owner, {
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
    })
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

const savers = new Map<string, DraftSaver>()

const draftKey = ({ clientSessionId, channel }: Pick<DraftTarget, 'clientSessionId' | 'channel'>) =>
  `${clientSessionId}.${channel}`

function saverFromOutbox(entry: OutboxEntry, target: DraftTarget, queryClient: QueryClient) {
  return new DraftSaver(
    { ...target, sessionId: entry.sessionId, baseRevision: entry.baseRevision, latestRevision: entry.latestRevision },
    queryClient,
    entry,
  )
}

export function draftSaverFor(target: DraftTarget, queryClient: QueryClient): DraftSaver {
  const key = draftKey(target)
  const existing = savers.get(key)
  if (existing) return existing
  const entry = readOutboxEntry(target.owner, key)
  const saver = entry ? saverFromOutbox(entry, target, queryClient) : new DraftSaver(target, queryClient)
  savers.set(key, saver)
  return saver
}

export function discardDraftSavers(clientSessionId: string) {
  for (const channel of ['raw', 'cleaned'] as const) {
    savers.get(draftKey({ clientSessionId, channel }))?.discard()
  }
}

export function resumeUnsentDrafts(owner: string, queryClient: QueryClient) {
  for (const entry of readOutbox(owner)) {
    if (!savers.has(entry.key)) savers.set(entry.key, saverFromOutbox(entry, { ...entry, owner }, queryClient))
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

/** True when the Server holds each word, and false when a word stays unsent or refused. */
export async function settleDraftSavers() {
  const settled = await Promise.all(everySaver().map((saver) => saver.settle()))
  return settled.every(Boolean)
}

/** The savers stay in the map, so that a render on the way out finds a stopped saver and does not start a new one from the outbox. */
export function stopDraftSavers() {
  for (const saver of everySaver()) saver.stop()
}

export function discardUnsentDrafts() {
  for (const saver of everySaver()) saver.discard()
}
