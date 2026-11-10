import type { ModelProviderDto } from '@/api/generated/models/types/model-provider-dto'
import type { ModelProviderListDto } from '@/api/generated/models/types/model-provider-list-dto'
import type { ModelProviderTestResultDto } from '@/api/generated/models/types/model-provider-test-result-dto'

import { aProblem } from '../builders/problem-details-builder'
import type { MockRoute } from './mock-route'

export const modelProviderPaths = {
  list: '/api/v1/model-providers',
  active: '/api/v1/model-providers/active',
  record: (id: string) => `/api/v1/model-providers/${id}`,
  test: (id: string) => `/api/v1/model-providers/${id}/test`,
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
}
