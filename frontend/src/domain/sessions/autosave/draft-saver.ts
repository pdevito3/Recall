import type { QueryClient } from '@tanstack/react-query'

import type { DraftSaveResultDto } from '@/api/generated/models/types/draft-save-result-dto'
import type { SessionDto } from '@/api/generated/models/types/session-dto'
import { addSession } from '@/api/generated/sessions/clients/add-session'
import { saveCleanedDraft } from '@/api/generated/sessions/clients/save-cleaned-draft'
import { saveRawDraft } from '@/api/generated/sessions/clients/save-raw-draft'

import { SessionKeys, type DraftChannel } from '../apis/session.keys'
import { patchTimelineRow, refreshTimelineHead } from '../apis/timeline-cache'
import { plainPreviewOf } from '../editor/content'
import { readOutbox, removeFromOutbox, writeToOutbox } from './outbox'
import { queueWithUnanswered, newestUnsettled, type SaveQueue, type SaveStatus, type Stamped } from './save-queue'
import { SerialSaver } from './serial-saver'

/**
 * `lost`: a newer save holds the Draft, and these words stay as a Revision.
 * `replaced`: these words hold the Draft, over a save that this page had not seen.
 */
export type DraftConflict = 'lost' | 'replaced' | null

export interface DraftSnapshot {
  status: SaveStatus
  conflict: DraftConflict
  sessionId: string | null
  baseRevision: number
}

export interface DraftTarget {
  owner: string
  channel: DraftChannel
  clientSessionId: string
  startedAt: string
  sessionId: string | null
  baseRevision: number
  /** Waits for a location, when the person turned on the capture. */
  location?: Promise<{ latitude: number; longitude: number } | null>
}

const pauseInTypingMs = 800
const previewLength = 200

/** The autosave of one text of one Session. It outlives the page, so a save goes on after the person leaves the page. */
export class DraftSaver {
  private readonly target: DraftTarget
  private readonly queryClient: QueryClient
  private readonly saver: SerialSaver<string, DraftSaveResultDto>
  private snapshot: DraftSnapshot
  private readonly listeners = new Set<() => void>()

  constructor(target: DraftTarget, queryClient: QueryClient, resumed?: Stamped<string>) {
    this.target = target
    this.queryClient = queryClient
    this.snapshot = { status: { kind: 'idle' }, conflict: null, sessionId: target.sessionId, baseRevision: target.baseRevision }
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

  /** The words that the Server has not answered, which a page shows in place of the words from the Server. */
  unsettledText() {
    return this.saver.newestUnsettledValue()
  }

  dismissConflict() {
    this.update({ conflict: null })
  }

  /** The page read a newer Revision from the Server and shows it, so the next save is based on it. */
  moveBaseTo(revision: number) {
    this.update({ baseRevision: revision })
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
    this.update({ sessionId: created.id, baseRevision: created.rawLatestRevision })
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
    const base = this.snapshot.baseRevision
    const conflict = conflictOf(reply, base)
    this.update({
      baseRevision: Math.max(base, reply.latestRevision),
      conflict: conflict ?? this.snapshot.conflict,
    })
    const id = this.snapshot.sessionId
    if (!id) return
    this.keepSessionCurrent(id, reply, change)
  }

  private keepSessionCurrent(id: string, reply: DraftSaveResultDto, change: Stamped<string>) {
    const words = reply.outcome === 'Won' ? { text: change.value, savedAt: reply.savedAt } : null
    this.queryClient.setQueryData<SessionDto>(SessionKeys.detail(id), (session) => {
      if (!session) return session
      const revisions = this.target.channel === 'raw'
        ? { rawDraftRevision: reply.draftRevision, rawLatestRevision: reply.latestRevision, ...(words && { rawText: words.text, rawSavedAt: words.savedAt }) }
        : { cleanedDraftRevision: reply.draftRevision, cleanedLatestRevision: reply.latestRevision, ...(words && { cleanedText: words.text, cleanedSavedAt: words.savedAt }) }
      return { ...session, ...revisions, cleanupStatus: reply.cleanupStatus, version: reply.version }
    })
    void this.queryClient.invalidateQueries({ queryKey: SessionKeys.revisions(id, this.target.channel) })
    if (this.target.channel === 'raw' && words) {
      patchTimelineRow(this.queryClient, id, {
        preview: plainPreviewOf(words.text, previewLength),
        cleanupStatus: reply.cleanupStatus,
        version: reply.version,
      })
    }
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
      change,
    })
  }
}

function conflictOf(reply: DraftSaveResultDto, base: number): DraftConflict {
  if (reply.outcome === 'Lost') return 'lost'
  // A save based on the newest Revision makes the next number. A higher number means that a save came between.
  if (reply.outcome === 'Won' && reply.draftRevision > base + 1) return 'replaced'
  return null
}

const savers = new Map<string, DraftSaver>()

const draftKey = ({ clientSessionId, channel }: Pick<DraftTarget, 'clientSessionId' | 'channel'>) =>
  `${clientSessionId}.${channel}`

export function draftSaverFor(target: DraftTarget, queryClient: QueryClient): DraftSaver {
  const key = draftKey(target)
  const existing = savers.get(key)
  if (existing) return existing
  const saver = new DraftSaver(target, queryClient)
  savers.set(key, saver)
  return saver
}

export function discardDraftSavers(clientSessionId: string) {
  for (const channel of ['raw', 'cleaned'] as const) {
    savers.get(draftKey({ clientSessionId, channel }))?.discard()
  }
}

/** Sends the words that an earlier page load did not get a reply for. */
export function resumeUnsentDrafts(owner: string, queryClient: QueryClient) {
  for (const entry of readOutbox(owner)) {
    if (savers.has(entry.key)) continue
    const target: DraftTarget = { ...entry, owner }
    savers.set(entry.key, new DraftSaver(target, queryClient, entry.change))
  }
}
