import type { ProblemDetails } from '@/lib/problem-details'
import type { Identity } from '../auth/actors'
import type { MockRoute } from './mock-route'

export const authPaths = {
  me: '/auth/me',
  login: '/auth/login',
  logout: '/auth/logout',
}

export const authApi = {
  /** The user of the session, or a 204 with no body for an anonymous visitor, as the Server answers. */
  userIs(identity: Identity): MockRoute {
    return identity.signedIn
      ? { method: 'get', path: authPaths.me, status: 200, body: identity.user }
      : { method: 'get', path: authPaths.me, status: 204 }
  },

  /** The Server fails to read the session. */
  userFails(problem: { build(): ProblemDetails }): MockRoute {
    const body = problem.build()
    return { method: 'get', path: authPaths.me, status: body.status ?? 500, body }
  },

  /** The auth session ended: `/auth/me` answers 204, and the Server answers 401 with no body to each call of the API that needs a user. */
  sessionEnded(...apiPaths: string[]): MockRoute[] {
    return [
      { method: 'get', path: authPaths.me, status: 204 },
      ...apiPaths.map((path): MockRoute => ({ method: 'get', path, status: 401 })),
    ]
  },

  /** The auth session lives, and `/auth/me` keeps its answer, but the Server answers 401 with no body to each call of the API. */
  apiRejectsTheToken(...apiPaths: string[]): MockRoute[] {
    return apiPaths.map((path): MockRoute => ({ method: 'get', path, status: 401 }))
  },

  /** The SPA navigates to the login of the Server. The mock answers with an empty page, not with the redirect to the IdP. */
  loginOpens(): MockRoute {
    return { method: 'get', path: authPaths.login, status: 200 }
  },

  /** The Server ends the session. The next page is the signed-out page, which the mock answers with an empty page. */
  logoutWorks(): MockRoute[] {
    return [{ method: 'post', path: authPaths.logout, status: 204 }, authApi.loginOpens()]
  },

  /** The Server cannot end the session. */
  logoutFails(): MockRoute {
    return { method: 'post', path: authPaths.logout, status: 500 }
  },
}
