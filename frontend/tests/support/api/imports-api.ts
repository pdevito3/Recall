import type { ImportBatchDto } from '@/api/generated/models/types/import-batch-dto'
import type { ImportDayDto } from '@/api/generated/models/types/import-day-dto'
import type { ImportEntryDto } from '@/api/generated/models/types/import-entry-dto'
import type { UserSettingDto } from '@/api/generated/models/types/user-setting-dto'
import type { ProblemDetails } from '@/lib/problem-details'
import type { MockRoute } from './mock-route'

export const importPaths = {
  list: '/api/v1/import-batches',
  one: (id: string) => `/api/v1/import-batches/${id}`,
  days: (id: string) => `/api/v1/import-batches/${id}/days`,
  entries: (id: string) => `/api/v1/import-batches/${id}/entries`,
  entryReview: (id: string) => `/api/v1/import-batches/${id}/entries/review`,
  dayReview: (id: string) => `/api/v1/import-batches/${id}/days/review`,
  accept: (id: string) => `/api/v1/import-batches/${id}/accept`,
  importReview: '/api/v1/me/settings/import-review',
}

export const anImportBatch = (id: string, name: string, patch: Partial<ImportBatchDto> = {}): ImportBatchDto => ({
  id,
  name,
  timeZone: 'America/New_York',
  state: 'Open',
  openedAt: '2026-11-20T15:00:00Z',
  acceptedAt: null,
  entryCount: 0,
  result: null,
  version: 1,
  ...patch,
})

export const documentOf = (text: string) => JSON.stringify({ type: 'doc', content: [{ type: 'paragraph', content: [{ type: 'text', text }] }] })

export const anImportEntry = (clientSessionId: string, date: string, text: string, patch: Partial<ImportEntryDto> = {}): ImportEntryDto => ({
  clientSessionId,
  date,
  position: 0,
  time: '08:30',
  timeZone: null,
  raw: text,
  document: documentOf(text),
  moods: [],
  topics: [],
  review: { rejected: false, words: null, time: null },
  isRejected: false,
  startsAt: `${date}T13:30:00Z`,
  wordsChangedAfterEdit: false,
  timeChangedAfterEdit: false,
  ...patch,
})

export const anImportDay = (date: string, entryCount: number, patch: Partial<ImportDayDto> = {}): ImportDayDto => ({
  date,
  entryCount,
  rejectedCount: 0,
  rejected: false,
  ...patch,
})

export const importsApi = {
  listIs(batches: ImportBatchDto[]): MockRoute {
    return { method: 'get', path: importPaths.list, status: 200, body: batches }
  },

  daysAre(id: string, days: ImportDayDto[], version = 1): MockRoute {
    return { method: 'get', path: importPaths.days(id), status: 200, body: { version, days } }
  },

  daysRefuse(id: string, problem: ProblemDetails): MockRoute {
    return { method: 'get', path: importPaths.days(id), status: problem.status ?? 500, body: problem }
  },

  entriesOfAnyRangeAre(id: string, entries: ImportEntryDto[], version = 1): MockRoute {
    return { method: 'get', path: importPaths.entries(id), status: 200, body: { version, entries, rejectedDays: [] } }
  },

  discards(id: string): MockRoute {
    return { method: 'delete', path: importPaths.one(id), status: 204 }
  },

  reviewsEntry(id: string, entry: ImportEntryDto, version = 2): MockRoute {
    return { method: 'put', path: importPaths.entryReview(id), status: 200, body: { version, entry } }
  },

  refusesEntryReview(id: string, problem: ProblemDetails): MockRoute {
    return { method: 'put', path: importPaths.entryReview(id), status: problem.status ?? 500, body: problem }
  },

  reviewsDay(id: string, date: string, rejected: boolean, version = 2): MockRoute {
    return { method: 'put', path: importPaths.dayReview(id), status: 200, body: { version, date, rejected } }
  },

  accepts(batch: ImportBatchDto): MockRoute {
    return { method: 'post', path: importPaths.accept(batch.id), status: 200, body: batch }
  },

  refusesAccept(id: string, problem: ProblemDetails): MockRoute {
    return { method: 'post', path: importPaths.accept(id), status: problem.status ?? 500, body: problem }
  },

  changesImportReview(setting: UserSettingDto): MockRoute {
    return { method: 'put', path: importPaths.importReview, status: 200, body: setting }
  },

  refusesImportReview(problem: ProblemDetails): MockRoute {
    return { method: 'put', path: importPaths.importReview, status: problem.status ?? 500, body: problem, once: true }
  },
}
