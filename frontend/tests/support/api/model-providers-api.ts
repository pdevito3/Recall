import type { ModelProviderDto } from '@/api/generated/models/types/model-provider-dto'
import type { ModelProviderListDto } from '@/api/generated/models/types/model-provider-list-dto'
import type { ModelProviderTestResultDto } from '@/api/generated/models/types/model-provider-test-result-dto'
import type { SessionDto } from '@/api/generated/models/types/session-dto'

import { aProblem } from '../builders/problem-details-builder'
import type { MockRoute } from './mock-route'

export const modelProviderPaths = {
  list: '/api/v1/model-providers',
  active: '/api/v1/model-providers/active',
  record: (id: string) => `/api/v1/model-providers/${id}`,
  test: (id: string) => `/api/v1/model-providers/${id}/test`,
  session: (id: string) => `/api/v1/sessions/${id}`,
  cleanup: (id: string) => `/api/v1/sessions/${id}/cleanup`,
}

export const aProvider = (changes: Partial<ModelProviderDto> = {}): ModelProviderDto => ({
  id: 'mpr_00000000000000000000000000000001',
  kind: 'Anthropic',
  name: 'Mine',
  baseUrl: null,
  model: null,
  isActive: true,
  keyState: 'Readable',
  version: 1,
  ...changes,
})

export const aSession = (changes: Partial<SessionDto> = {}): SessionDto =>
  ({
    id: 'ses_00000000000000000000000000000001',
    rawText: 'um I met sam today',
    cleanedText: '',
    cleanupStatus: 'NotRun',
    cleanupFailure: 'None',
    cleanedHasHandEdits: false,
    suggestions: [],
    peopleProposals: [],
    ...changes,
  }) as SessionDto

export const modelProvidersApi = {
  activeIs(name: string | null): MockRoute {
    return { method: 'get', path: modelProviderPaths.active, status: 200, body: { name } }
  },

  listHolds(items: ModelProviderDto[], keyRing: ModelProviderListDto['keyRing'] = 'Same'): MockRoute {
    return { method: 'get', path: modelProviderPaths.list, status: 200, body: { items, keyRing } satisfies ModelProviderListDto }
  },

  addAnswers(provider: ModelProviderDto): MockRoute {
    return { method: 'post', path: modelProviderPaths.list, status: 200, body: provider }
  },

  addRefuses(errors: Record<string, string[]>): MockRoute {
    return { method: 'post', path: modelProviderPaths.list, status: 422, body: aProblem().invalid(errors).build() }
  },

  updateAnswers(provider: ModelProviderDto): MockRoute {
    return { method: 'put', path: modelProviderPaths.record(provider.id), status: 200, body: provider }
  },

  testAnswers(provider: ModelProviderDto, result: ModelProviderTestResultDto): MockRoute {
    return { method: 'post', path: modelProviderPaths.test(provider.id), status: 200, body: result }
  },

  sessionIs(session: SessionDto): MockRoute {
    return { method: 'get', path: modelProviderPaths.session(session.id), status: 200, body: session }
  },

  cleanupStarts(session: SessionDto): MockRoute {
    return { method: 'post', path: modelProviderPaths.cleanup(session.id), status: 202, body: session }
  },

  cleanupRefuses(session: SessionDto, message: string): MockRoute {
    return { method: 'post', path: modelProviderPaths.cleanup(session.id), status: 422, body: aProblem().invalid({ cleanup: [message] }).build() }
  },
}
