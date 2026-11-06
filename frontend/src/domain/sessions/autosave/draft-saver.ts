import type { QueryClient } from '@tanstack/react-query'

import type { DraftSaveResultDto } from '@/api/generated/models/types/draft-save-result-dto'
import type { SessionDto } from '@/api/generated/models/types/session-dto'
import { addSession } from '@/api/generated/sessions/clients/add-session'
import { saveCleanedDraft } from '@/api/generated/sessions/clients/save-cleaned-draft'
import { saveRawDraft } from '@/api/generated/sessions/clients/save-raw-draft'
import { Notification } from '@/components/notification'

import { sessionQueryOptions } from '../apis/get-session'
import { SessionKeys, type DraftChannel } from '../apis/session.keys'
import { patchTimelineRow, refreshTimelineHead } from '../apis/timeline-cache'
import { plainPreviewOf } from '../editor/content'
import { readOutbox, readOutboxEntry, removeFromOutbox, writeToOutbox, type OutboxEntry } from './outbox'
import { queueWithUnanswered, newestUnsettled, type SaveQueue, type SaveStatus, type Stamped } from './save-queue'
import { SerialSaver } from './serial-saver'

export type DraftConflict = 'wordsMovedToHistory' | 'replacedUnseenSave' | null

export type ShownSaveStatus = SaveStatus | { kind: 'savedInHistory'; at: string }

export interface DraftSnapshot {
  status: SaveStatus
  conflict: DraftConflict
  sessionId: string | null
  baseRevision: number
  latestRevision: number
}

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
  private readonly target: DraftTarget
  private readonly queryClient: QueryClient
  private readonly saver: SerialSaver<string, DraftSaveResultDto>
  private snapshot: DraftSnapshot
  private readonly listeners = new Set<() => void>()

  constructor(target: DraftTarget, queryClient: QueryClient, resumed?: Stamped<string>) {
    this.target = target
    this.queryClient = queryClient
    this.snapshot = {
      status: { kind: 'idle' },
      conflict: null,
      sessionId: target.sessionId,
      baseRevision: target.baseRevision,
      latestRevision: target.latestRevision,
    }
    this.saver = new SerialSaver({
      debounceMs: pauseInTypingMs,
      send: (change) => this.send(change),
      onReply: (reply, change) => this.handleReply(reply, change),
      onQueueChange: (queue) => this.keepInOutbox(queue),
      initialQueue: resumed && queueWithUnanswered(resumed),
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

  unsettledText() {
    return this.saver.newestUnsettledValue()
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
    const newest = id && (await this.queryClient.fetchQuery({ ...sessionQueryOptions(id), staleTime: 0 }).catch(() => null))
    if (!newest) {
      Notification.error('The newest words did not load. Try again.')
      return null
    }
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
    savers.delete(draftKey(this.target))
  }

  subscribe = (listener: () => void) => {
    this.listeners.add(listener)
    return () => {
      this.listeners.delete(listener)
    }
  }

  getSnapshot = () => this.snapshot

  private update(patch: Partial<DraftSnapshot>) {
    this.snapshot = { ...this.snapshot, ...patch }
    for (const listener of this.listeners) listener()
  }

  private async sessionIdForSave() {
    if (this.snapshot.sessionId) return this.snapshot.sessionId
    const location = await this.target.location
    const created = await addSession({
      body: {
        clientSessionId: this.target.clientSessionId,
        startedAt: this.target.startedAt,
        latitude: location?.latitude ?? null,
        longitude: location?.longitude ?? null,
      },
      options: { suppressErrorToast: true },
    })
    this.afterCreate(created)
    return created.id
  }

  private afterCreate(created: SessionDto) {
    this.update({ sessionId: created.id, baseRevision: created.rawDraftRevision, latestRevision: created.rawLatestRevision })
    this.queryClient.setQueryData(SessionKeys.detail(created.id), created)
    void refreshTimelineHead(this.queryClient)
  }

  private async send(change: Stamped<string>) {
    const id = await this.sessionIdForSave()
    const body = { text: change.value, baseRevision: this.snapshot.baseRevision, savedAt: change.savedAt }
    const options = { suppressErrorToast: true }
    return this.target.channel === 'raw'
      ? saveRawDraft({ path: { id }, body, options })
      : saveCleanedDraft({ path: { id }, body, options })
  }

  private handleReply(reply: DraftSaveResultDto, change: Stamped<string>) {
    const { baseRevision, latestRevision, conflict } = this.snapshot
    const holdsWords = draftHoldsTheseWords(reply, baseRevision, latestRevision)
    if (holdsWords || reply.outcome === 'Lost') {
      this.update({ baseRevision: reply.draftRevision, latestRevision: Math.max(latestRevision, reply.latestRevision) })
    }
    this.update({ conflict: conflictAfter(reply, holdsWords, latestRevision, conflict) })
    const id = this.snapshot.sessionId
    if (!id) return
    this.keepSessionCurrent(id, reply, holdsWords ? change.value : null)
    void this.queryClient.invalidateQueries({ queryKey: SessionKeys.revisions(id, this.target.channel) })
    if (!holdsWords) void this.readNewest(id)
  }

  private keepSessionCurrent(id: string, reply: DraftSaveResultDto, words: string | null) {
    const key = SessionKeys.detail(id)
    const isLoaded = this.queryClient.getQueryData(key) !== undefined
    void (isLoaded ? this.queryClient.cancelQueries({ queryKey: key, exact: true }) : this.queryClient.invalidateQueries({ queryKey: key, exact: true }))
    this.queryClient.setQueryData<SessionDto>(key, (session) => {
      if (!session) return session
      const isRaw = this.target.channel === 'raw'
      const latest = isRaw ? { rawLatestRevision: reply.latestRevision } : { cleanedLatestRevision: reply.latestRevision }
      const draft = words === null
        ? {}
        : isRaw
          ? { rawDraftRevision: reply.draftRevision, rawText: words, rawSavedAt: reply.savedAt }
          : { cleanedDraftRevision: reply.draftRevision, cleanedText: words, cleanedSavedAt: reply.savedAt }
      return { ...session, ...latest, ...draft, cleanupStatus: reply.cleanupStatus, version: reply.version }
    })
    if (words !== null && this.target.channel === 'raw') this.patchRow(id, words, reply)
  }

  private async readNewest(id: string) {
    const newest = await this.queryClient.fetchQuery({ ...sessionQueryOptions(id), staleTime: 0 }).catch(() => null)
    if (newest && this.target.channel === 'raw') this.patchRow(id, newest.rawText, newest)
  }

  private patchRow(id: string, words: string, current: Pick<SessionDto, 'cleanupStatus' | 'version'>) {
    patchTimelineRow(this.queryClient, id, {
      preview: plainPreviewOf(words, previewLength),
      cleanupStatus: current.cleanupStatus,
      version: current.version,
    })
  }

  private keepInOutbox(queue: SaveQueue<string>) {
    const key = draftKey(this.target)
    const change = newestUnsettled(queue)
    if (!change) {
      removeFromOutbox(this.target.owner, key)
      return
    }
    writeToOutbox(this.target.owner, {
      key,
      channel: this.target.channel,
      clientSessionId: this.target.clientSessionId,
      startedAt: this.target.startedAt,
      sessionId: this.snapshot.sessionId,
      baseRevision: this.snapshot.baseRevision,
      latestRevision: this.snapshot.latestRevision,
      change,
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
    entry.change,
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
}
