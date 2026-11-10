import type { Identity } from '../auth/actors'
import { actorSubject } from '../auth/actors'
import { aProblem } from '../builders/problem-details-builder'
import { aUser } from '../builders/user-builder'
import type { MockRoute } from './mock-route'

export const membershipPaths = {
  userOf: (identifier: string) => `/api/v1/users/by-identifier/${identifier}`,
}

export const membershipApi = {
  /** No route for an anonymous actor, because the SPA does not look up a user record without a session. */
  userIs(identity: Identity): MockRoute[] {
    if (!identity.signedIn) return []
    return [{ method: 'get', path: membershipPaths.userOf(actorSubject), status: 200, body: aUser().identifiedBy(actorSubject).build() }]
  },

  /** A person whose role lets them read the list of users. */
  userMayReadUsers(): MockRoute {
    const body = { ...aUser().identifiedBy(actorSubject).inRole('Admin').build(), permissions: ['read_users' as const] }
    return { method: 'get', path: membershipPaths.userOf(actorSubject), status: 200, body }
  },

  /** The Server refuses the lookup, as it does for a deleted user whose tenant it still has in its cache. */
  lookupRefuses(detail: string): MockRoute {
    const body = { ...aProblem().saying(detail).build(), status: 403 }
    return { method: 'get', path: membershipPaths.userOf(actorSubject), status: 403, body }
  },
}
