import type { ProblemDetails } from '@/lib/problem-details'
import type { Identity } from '../auth/actors'
import type { MockRoute } from './mock-route'

export const authPaths = {
  me: '/auth/me',
  login: '/auth/login',
  logout: '/auth/logout',
}

export const authApi = {
  userIs(identity: Identity): MockRoute {
    return identity.signedIn
      ? { method: 'get', path: authPaths.me, status: 200, body: identity.user }
      : { method: 'get', path: authPaths.me, status: 204 }
  },

  userFails(problem: { build(): ProblemDetails }): MockRoute {
    const body = problem.build()
    return { method: 'get', path: authPaths.me, status: body.status ?? 500, body }
  },

  sessionEnded(...apiPaths: string[]): MockRoute[] {
    return [
      { method: 'get', path: authPaths.me, status: 204 },
      ...apiPaths.map((path): MockRoute => ({ method: 'get', path, status: 401 })),
    ]
  },

  apiAnswers401WhileTheSessionLives(...apiPaths: string[]): MockRoute[] {
    return apiPaths.map((path): MockRoute => ({ method: 'get', path, status: 401 }))
  },

  loginOpens(): MockRoute {
    return { method: 'get', path: authPaths.login, status: 200 }
  },

  logoutWorks(): MockRoute[] {
    return [{ method: 'post', path: authPaths.logout, status: 204 }, authApi.loginOpens()]
  },

  logoutFails(): MockRoute {
    return { method: 'post', path: authPaths.logout, status: 500 }
  },
}
