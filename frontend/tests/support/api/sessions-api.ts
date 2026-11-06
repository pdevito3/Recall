import type { DraftSaveResultDto } from '@/api/generated/models/types/draft-save-result-dto'
import type { RevisionDto } from '@/api/generated/models/types/revision-dto'
import type { SessionDto } from '@/api/generated/models/types/session-dto'
import type { SessionSummaryDto } from '@/api/generated/models/types/session-summary-dto'
import type { ProblemDetails } from '@/lib/problem-details'
import type { MockRoute } from './mock-route'

export const sessionsPaths = {
  timeline: '/api/v1/sessions',
  one: (id: string) => `/api/v1/sessions/${id}`,
  rawDraft: (id: string) => `/api/v1/sessions/${id}/draft`,
  metadata: (id: string) => `/api/v1/sessions/${id}/metadata`,
  rawRevisions: (id: string) => `/api/v1/sessions/${id}/raw-revisions`,
  rawRevision: (id: string, number: number) => `/api/v1/sessions/${id}/raw-revisions/${number}`,
}

const won = (session: SessionDto, revision: number): DraftSaveResultDto => ({
  outcome: 'Won',
  draftRevision: revision,
  latestRevision: revision,
  savedAt: '2026-11-06T15:31:00+00:00',
  cleanupStatus: session.cleanupStatus,
  version: session.version + 1,
})

export const sessionsApi = {
  timelineIs(rows: SessionSummaryDto[], nextCursor: string | null = null): MockRoute {
    return { method: 'get', path: sessionsPaths.timeline, status: 200, body: { items: rows, nextCursor } }
  },

  timelineFails(problem: { build(): ProblemDetails }): MockRoute {
    const body = problem.build()
    return { method: 'get', path: sessionsPaths.timeline, status: body.status ?? 500, body }
  },

  creates(session: SessionDto): MockRoute {
    return { method: 'post', path: sessionsPaths.timeline, status: 201, body: session }
  },

  createIsRefused(problem: { build(): ProblemDetails }): MockRoute {
    const body = problem.build()
    return { method: 'post', path: sessionsPaths.timeline, status: body.status ?? 422, body }
  },

  oneIs(session: SessionDto): MockRoute {
    return { method: 'get', path: sessionsPaths.one(session.id), status: 200, body: session }
  },

  oneFails(id: string, problem: { build(): ProblemDetails }): MockRoute {
    const body = problem.build()
    return { method: 'get', path: sessionsPaths.one(id), status: body.status ?? 500, body }
  },

  deletes(id: string): MockRoute {
    return { method: 'delete', path: sessionsPaths.one(id), status: 204 }
  },

  draftSaveWins(session: SessionDto, revision: number): MockRoute {
    return { method: 'put', path: sessionsPaths.rawDraft(session.id), status: 200, body: won(session, revision) }
  },

  draftSaveLoses(session: SessionDto, { draftRevision, latestRevision }: { draftRevision: number; latestRevision: number }): MockRoute {
    return {
      method: 'put',
      path: sessionsPaths.rawDraft(session.id),
      status: 200,
      body: { ...won(session, draftRevision), outcome: 'Lost', draftRevision, latestRevision },
    }
  },

  draftSaveIsUnchanged(session: SessionDto, { draftRevision, latestRevision }: { draftRevision: number; latestRevision: number }): MockRoute {
    return {
      method: 'put',
      path: sessionsPaths.rawDraft(session.id),
      status: 200,
      body: { ...won(session, draftRevision), outcome: 'Unchanged', draftRevision, latestRevision },
    }
  },

  draftSaveHasNoReply(session: SessionDto): MockRoute {
    return { method: 'put', path: sessionsPaths.rawDraft(session.id), status: 503 }
  },

  draftSaveIsRefused(session: SessionDto, problem: { build(): ProblemDetails }): MockRoute {
    const body = problem.build()
    return { method: 'put', path: sessionsPaths.rawDraft(session.id), status: body.status ?? 422, body }
  },

  metadataSaves(session: SessionDto): MockRoute {
    return { method: 'put', path: sessionsPaths.metadata(session.id), status: 200, body: session }
  },

  revisionsAre(session: SessionDto, revisions: RevisionDto[]): MockRoute[] {
    return [
      {
        method: 'get',
        path: sessionsPaths.rawRevisions(session.id),
        status: 200,
        body: {
          items: revisions.map(({ number, savedAt, text }) => ({ number, savedAt, length: text.length })),
          nextCursor: null,
        },
      },
      ...revisions.map(
        (revision): MockRoute => ({
          method: 'get',
          path: sessionsPaths.rawRevision(session.id, revision.number),
          status: 200,
          body: revision,
        }),
      ),
    ]
  },
}
