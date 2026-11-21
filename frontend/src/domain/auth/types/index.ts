/**
 * Auth domain types
 */

export interface AuthUser {
  sub: string
  name: string | null
  sid: string
  client_kind: string
  /** The login name of the password credential of the person, or null for a person with no password (PWD-17). */
  login_name?: string | null
}

export interface AuthState {
  isLoggedIn: boolean
  isLoading: boolean
  username: string | null
}
