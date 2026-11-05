/**
 * Auth domain types
 */

/** The answer of `GET /auth/me`. */
export interface AuthUser {
  sub: string
  name: string | null
  sid: string
  client_kind: string
}

/**
 * The auth state that the page reads
 */
export interface AuthState {
  isLoggedIn: boolean
  isLoading: boolean
  username: string | null
}
