import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'

import { getExternalLogins } from '@/api/generated/external-logins/clients/get-external-logins'
import { getUserExternalLogins } from '@/api/generated/external-logins/clients/get-user-external-logins'
import { removeExternalLogin } from '@/api/generated/external-logins/clients/remove-external-login'
import { removeUserExternalLogin } from '@/api/generated/external-logins/clients/remove-user-external-login'
import type { ExternalLoginForRemovalDto } from '@/api/generated/models/types/external-login-for-removal-dto'
import { leaveTheApp } from '@/domain/auth/apis/leave-the-app'
import { apiClient } from '@/lib/api-client'
import { retryUnlessRefused } from '@/lib/problem-details'

import { ExternalLoginKeys } from './external-login.keys'

export function useMyExternalLogins() {
  return useQuery({
    queryKey: ExternalLoginKeys.mine(),
    queryFn: ({ signal }) => getExternalLogins({ signal, options: { suppressErrorToast: true } }),
    retry: retryUnlessRefused,
  })
}

export function useUserExternalLogins(userId: string, enabled: boolean) {
  return useQuery({
    queryKey: ExternalLoginKeys.ofUser(userId),
    queryFn: ({ signal }) => getUserExternalLogins({ path: { id: userId }, signal, options: { suppressErrorToast: true } }),
    retry: retryUnlessRefused,
    enabled,
  })
}

/** Removes a link of the caller, or of the user with `userId`. The 409 of the last Admin shows at the link, so it has no toast. */
export function useRemoveExternalLogin(userId?: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (body: ExternalLoginForRemovalDto) =>
      userId
        ? removeUserExternalLogin({ path: { id: userId }, body, options: { suppressErrorToast: true } })
        : removeExternalLogin({ body, options: { suppressErrorToast: true } }),
    onSettled: () => queryClient.invalidateQueries({ queryKey: ExternalLoginKeys.lists() }),
  })
}

export interface LoginMode {
  kind: string
  id: string
  displayName: string
  state: 'ready' | 'unreachable'
}

interface LoginStatus {
  modes: LoginMode[]
}

/** The providers of the login page. `/auth/status` is an /auth route, so it is not in the OpenAPI document. */
export function useLoginProviderModes() {
  return useQuery({
    queryKey: ExternalLoginKeys.providers(),
    queryFn: async ({ signal }) => {
      const { data } = await apiClient.get<LoginStatus>('/auth/status', { signal, suppressErrorToast: true })
      return data.modes.filter((mode) => mode.kind === 'oidc')
    },
  })
}

/**
 * Starts a link at the provider (IDP-9). The Server answers with the URL of the provider, and the page leaves for it, as
 * each way out of the app does, so words that only this tab holds show first. The provider comes back to settings.
 */
export function useStartLink() {
  return useMutation({
    mutationFn: async (providerId: string) => {
      const { data } = await apiClient.post<{ url: string }>(`/auth/oidc/${encodeURIComponent(providerId)}/link`, undefined, { suppressErrorToast: true })
      return data.url
    },
    onSuccess: (url) => leaveTheApp({ why: 'linkProvider', to: url }),
  })
}
