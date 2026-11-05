import type { AuthUser } from '@/domain/auth/types'

// The SPA knows the user only from GET /auth/me. The api fixture mocks that endpoint with the user of the actor,
// so a test never drives a login form.
export type Identity = { signedIn: false } | { signedIn: true; user: AuthUser }

export const actorSubject = '1'

export const as = {
  anonymous: (): Identity => ({ signedIn: false }),
  /** A user with a user record, and so a tenant, on the Server. */
  user: (name = 'Bob Smith'): Identity => ({
    signedIn: true,
    user: { sub: actorSubject, name, sid: 'test-session', client_kind: 'web' },
  }),
}
