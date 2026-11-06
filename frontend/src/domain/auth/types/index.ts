/**
 * Auth domain types
 */

export interface AuthUser {
  sub: string
  name: string | null
  sid: string
  client_kind: string
}

export interface AuthState {
  isLoggedIn: boolean
  isLoading: boolean
  username: string | null
}
