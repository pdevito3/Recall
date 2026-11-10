import type { SessionDto } from '@/api/generated/models/types/session-dto'
import type { SessionSummaryDto } from '@/api/generated/models/types/session-summary-dto'

let seed = 0
const next = () => ++seed

export const documentWith = (...paragraphs: string[]) =>
  JSON.stringify({
    type: 'doc',
    content: paragraphs.map((text) => ({ type: 'paragraph', content: [{ type: 'text', text }] })),
  })

class SessionBuilder {
  private readonly session: SessionDto

  private constructor(session: SessionDto) {
    this.session = session
  }

  static create() {
    const number = next()
    return new SessionBuilder({
      id: `ses_${number}`,
      clientSessionId: `00000000-0000-4000-8000-${String(number).padStart(12, '0')}`,
      startedAt: '2026-11-06T15:30:00+00:00',
      journalingDay: '2026-11-06',
      rawText: documentWith('The words of this entry.'),
      rawDraftRevision: 1,
      rawLatestRevision: 1,
      rawSavedAt: '2026-11-06T15:30:00+00:00',
      cleanedText: '',
      cleanedDraftRevision: 0,
      cleanedLatestRevision: 0,
      cleanedSavedAt: null,
      cleanedHasHandEdits: false,
      synopsis: null,
      cleanupStatus: 'NotRun',
      lastCleanupRawRevision: 0,
      cleanupFailure: 'None',
      suggestions: [],
      peopleProposals: [],
      activity: 'None',
      moods: [],
      topics: [],
      personIds: [],
      latitude: null,
      longitude: null,
      version: 1,
      metadataVersion: 1,
    })
  }

  private with(patch: Partial<SessionDto>) {
    return new SessionBuilder({ ...this.session, ...patch })
  }

  saying(...paragraphs: string[]) {
    return this.with({ rawText: documentWith(...paragraphs) })
  }

  withPlainText(rawText: string) {
    return this.with({ rawText })
  }

  atRevision(revision: number) {
    return this.with({ rawDraftRevision: revision, rawLatestRevision: revision })
  }

  onDay(journalingDay: string) {
    return this.with({ journalingDay, startedAt: `${journalingDay}T15:30:00+00:00` })
  }

  tagged({ activity, moods, topics }: { activity?: string; moods?: string[]; topics?: string[] }) {
    return this.with({
      activity: activity ?? 'None',
      moods: moods ?? [],
      topics: (topics ?? []).map((name) => ({ name, provenance: 'UserSet' as const })),
    })
  }

  withSuggestedTopic(name: string) {
    return this.with({ topics: [...this.session.topics, { name, provenance: 'AiSuggested' }] })
  }

  mentioning(...personIds: string[]) {
    return this.with({ personIds })
  }

  cleaned(words: string, revision = 1) {
    return this.with({
      cleanedText: documentWith(words),
      cleanedDraftRevision: revision,
      cleanedLatestRevision: revision,
      cleanupStatus: 'Clean',
      cleanupFailure: 'None',
      cleanupBaseCleanedRevision: revision - 1,
    })
  }

  /** A run that started from the Cleaned Draft of the builder, so its result replaces Cleaned. */
  cleanupRunning() {
    return this.with({ cleanupStatus: 'Running', cleanupFailure: 'None', cleanupBaseCleanedRevision: this.session.cleanedDraftRevision })
  }

  cleanupFailed(failure: SessionDto['cleanupFailure']) {
    return this.with({ cleanupStatus: 'Failed', cleanupFailure: failure })
  }

  /** Raw is newer than the last Cleanup, and a later run failed. */
  staleAfterAFailedRun(failure: SessionDto['cleanupFailure']) {
    return this.with({ cleanupStatus: 'Stale', cleanupFailure: failure })
  }

  cleanedByHand() {
    return this.with({ cleanedHasHandEdits: true })
  }

  suggesting(...suggestions: SessionDto['suggestions']) {
    return this.with({ suggestions })
  }

  proposing(...labels: string[]) {
    return this.with({ peopleProposals: labels.map((label) => ({ label, matchedPersonId: null, isNew: true, contexts: [] })) })
  }

  withSynopsis(synopsis: string) {
    return this.with({ synopsis })
  }

  build(): SessionDto {
    return this.session
  }

  summary(preview: string): SessionSummaryDto {
    const { id, clientSessionId, startedAt, journalingDay, synopsis, cleanupStatus, cleanupFailure, activity, moods, topics, personIds, version, metadataVersion } =
      this.session
    return { id, clientSessionId, startedAt, journalingDay, preview, synopsis, cleanupStatus, cleanupFailure, activity, moods, topics, personIds, version, metadataVersion }
  }
}

export const aSession = () => SessionBuilder.create()
