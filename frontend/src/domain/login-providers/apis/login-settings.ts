import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'

import { getLoginSettings } from '@/api/generated/login-providers/clients/get-login-settings'
import { updateLoginSettings } from '@/api/generated/login-providers/clients/update-login-settings'
import type { LoginSettingsForUpdateDto } from '@/api/generated/models/types/login-settings-for-update-dto'
import { AuthKeys } from '@/domain/auth/apis/auth.keys'
import { retryUnlessRefused } from '@/lib/problem-details'

import { LoginProviderKeys } from './login-providers.keys'

/** Whether password login is On for the install (IDP-13). The page shows a failed load, so it has no toast. */
export function useLoginSettings() {
  return useQuery({
    queryKey: LoginProviderKeys.settings(),
    queryFn: ({ signal }) => getLoginSettings({ signal, options: { suppressErrorToast: true } }),
    retry: retryUnlessRefused,
  })
}

/**
 * Turns password login On or Off with the version that the page read (IDP-13). The card shows a refusal, so it has no toast.
 * A turn changes whether `/auth/me` gives a login name (IDP-15), so the user reads again, and settings shows or hides the
 * change of a password. After a refusal the setting and the user read again, because a stale version means that another Admin
 * turned it, and that turn changed the login name too.
 */
export function useUpdateLoginSettings() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (body: LoginSettingsForUpdateDto) => updateLoginSettings({ body, options: { suppressErrorToast: true } }),
    onMutate: () => queryClient.cancelQueries({ queryKey: LoginProviderKeys.settings() }),
    onSuccess: (settings) => {
      queryClient.setQueryData(LoginProviderKeys.settings(), settings)
      return queryClient.invalidateQueries({ queryKey: AuthKeys.user() })
    },
    onError: () =>
      Promise.all([
        queryClient.invalidateQueries({ queryKey: LoginProviderKeys.settings() }),
        queryClient.invalidateQueries({ queryKey: AuthKeys.user() }),
      ]),
  })
}
