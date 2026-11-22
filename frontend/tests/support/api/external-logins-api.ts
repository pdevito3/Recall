import type { ExternalLoginDto } from '@/api/generated/models/types/external-login-dto'
import type { ProblemDetails } from '@/lib/problem-details'
import type { MockRoute } from './mock-route'

export const externalLoginsPaths = {
  mine: '/api/v1/me/external-logins',
  ofUser: (userId: string) => `/api/v1/users/${userId}/external-logins`,
  status: '/auth/status',
  link: (providerId: string) => `/auth/oidc/${providerId}/link`,
}

export const aLink = (patch: Partial<ExternalLoginDto> = {}): ExternalLoginDto => ({
  issuer: 'https://idp.example',
  subject: 'sub-1',
  displayLabel: 'bob@work.example',
  providerId: 'work',
  providerName: 'Work SSO',
  createdAt: '2026-11-20T10:00:00Z',
  lastLoginAt: '2026-11-21T09:30:00Z',
  ...patch,
})

export interface StatusProvider {
  id: string
  displayName: string
  state?: 'ready' | 'unreachable'
}

export const externalLoginsApi = {
  mineAre(links: ExternalLoginDto[]): MockRoute {
    return { method: 'get', path: externalLoginsPaths.mine, status: 200, body: links }
  },

  ofUserAre(userId: string, links: ExternalLoginDto[]): MockRoute {
    return { method: 'get', path: externalLoginsPaths.ofUser(userId), status: 200, body: links }
  },

  removesMine(): MockRoute {
    return { method: 'delete', path: externalLoginsPaths.mine, status: 204 }
  },

  removesOfUser(userId: string): MockRoute {
    return { method: 'delete', path: externalLoginsPaths.ofUser(userId), status: 204 }
  },

  removeOfMineFails(problem: ProblemDetails): MockRoute {
    return { method: 'delete', path: externalLoginsPaths.mine, status: problem.status ?? 409, body: problem }
  },

  providersAre(providers: StatusProvider[]): MockRoute {
    return {
      method: 'get',
      path: externalLoginsPaths.status,
      status: 200,
      body: {
        instance: 'recall',
        state: 'ready',
        callbackUrl: 'http://localhost/auth/oidc/callback',
        message: null,
        setupRequired: false,
        modes: providers.map((p) => ({
          kind: 'oidc',
          id: p.id,
          displayName: p.displayName,
          state: p.state ?? 'ready',
          detail: null,
          startUrl: `/auth/oidc/${p.id}/start?client=app`,
        })),
      },
    }
  },

  linkStarts(providerId: string, url: string): MockRoute {
    return { method: 'post', path: externalLoginsPaths.link(providerId), status: 200, body: { url } }
  },

  linkRefused(providerId: string, problem: ProblemDetails): MockRoute {
    return { method: 'post', path: externalLoginsPaths.link(providerId), status: problem.status ?? 403, body: problem }
  },
}
