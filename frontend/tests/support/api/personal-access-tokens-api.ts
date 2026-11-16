import type { PersonalAccessTokenCreatedDto } from '@/api/generated/models/types/personal-access-token-created-dto'
import type { PersonalAccessTokenDto } from '@/api/generated/models/types/personal-access-token-dto'
import { aProblem } from '../builders/problem-details-builder'
import type { MockRoute } from './mock-route'

interface Buildable<T> {
  build(): T
}

export const personalAccessTokensPaths = {
  mine: '/api/v1/personal-access-tokens',
  ofUser: (userId: string) => `/api/v1/users/${userId}/personal-access-tokens`,
  revoke: (id: string) => `/api/v1/personal-access-tokens/${id}/revoke`,
}

export const personalAccessTokensApi = {
  mineAre(tokens: Buildable<PersonalAccessTokenDto>[]): MockRoute {
    return { method: 'get', path: personalAccessTokensPaths.mine, status: 200, body: tokens.map((t) => t.build()) }
  },

  ofUserAre(userId: string, tokens: Buildable<PersonalAccessTokenDto>[]): MockRoute {
    return { method: 'get', path: personalAccessTokensPaths.ofUser(userId), status: 200, body: tokens.map((t) => t.build()) }
  },

  /** No user has a token, for a page of a user whose tokens the test is not about. */
  noneForAnyUser(): MockRoute {
    return { method: 'get', path: /^\/api\/v1\/users\/[^/]+\/personal-access-tokens$/, status: 200, body: [] }
  },

  creates(created: PersonalAccessTokenCreatedDto): MockRoute {
    return { method: 'post', path: personalAccessTokensPaths.mine, status: 201, body: created }
  },

  createRefuses(detail: string): MockRoute {
    return { method: 'post', path: personalAccessTokensPaths.mine, status: 403, body: aProblem().forbidden(detail).build() }
  },

  revokes(id: string): MockRoute {
    return { method: 'post', path: personalAccessTokensPaths.revoke(id), status: 204 }
  },
}
