import { queryOptions, useQuery } from '@tanstack/react-query'
import { apiClient, sessionCheckPath } from '@/lib/api-client'
import type { AuthState, AuthUser } from '../types'
import { AuthKeys } from './auth.keys'

const isAuthUser = (data: unknown): data is AuthUser =>
  typeof data === 'object' && data !== null && 'sub' in data && typeof data.sub === 'string'

/**
 * Fetches the user of the session. Returns null when nobody is signed in: the Server answers 204 for an
 * anonymous visitor. A 4xx or 5xx status is an error with no toast, because the root route shows
 * the error.
 */
export async function getUser(): Promise<AuthUser | null> {
  const response = await apiClient.get<unknown>(sessionCheckPath, { suppressErrorToast: true })
  if (response.status === 204) return null
  if (!isAuthUser(response.data)) {
    throw new Error(`GET ${sessionCheckPath} answered ${response.status} with a body that is not a user.`)
  }
  return response.data
}

export const authUserQuery = queryOptions({
  queryKey: AuthKeys.user(),
  queryFn: getUser,
  staleTime: 5 * 60 * 1000,
})

/**
 * Hook for fetching and managing auth state
 */
export function useAuth(): AuthState {
  const { data, isLoading } = useQuery(authUserQuery)

  return {
    isLoggedIn: !!data,
    isLoading,
    username: data?.name ?? null,
  }
}
