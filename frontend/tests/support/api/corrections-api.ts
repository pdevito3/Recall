import type { CorrectionDto } from '@/api/generated/models/types/correction-dto'
import { aProblem } from '../builders/problem-details-builder'
import type { MockRoute } from './mock-route'
import { paginationHeaders } from './pagination'

export const correctionsPaths = {
  list: '/api/v1/corrections',
  one: (id: string) => `/api/v1/corrections/${id}`,
}

export const aCorrection = (id: string, canonicalTerm: string, mishearings: string[] = [], hardReplace = false): CorrectionDto => ({
  id,
  canonicalTerm,
  mishearings,
  hardReplace,
  version: 1,
})

export const correctionsApi = {
  listIs(corrections: CorrectionDto[]): MockRoute {
    return { method: 'get', path: correctionsPaths.list, status: 200, headers: paginationHeaders(corrections.length, 1), body: corrections }
  },

  adds(correction: CorrectionDto): MockRoute {
    return { method: 'post', path: correctionsPaths.list, status: 201, body: correction }
  },

  addRefuses(field: string, message: string): MockRoute {
    return {
      method: 'post',
      path: correctionsPaths.list,
      status: 422,
      body: aProblem().invalid({ [field]: [message] }).build(),
    }
  },

  updates(correction: CorrectionDto): MockRoute {
    return { method: 'put', path: correctionsPaths.one(correction.id), status: 200, body: correction }
  },

  deletes(id: string): MockRoute {
    return { method: 'delete', path: correctionsPaths.one(id), status: 204 }
  },

  deleteIsStale(id: string): MockRoute {
    return { method: 'delete', path: correctionsPaths.one(id), status: 409, body: aProblem().staleVersion().build() }
  },
}
