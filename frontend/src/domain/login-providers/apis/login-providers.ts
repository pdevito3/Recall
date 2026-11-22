import { queryOptions, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'

import { addLoginProvider } from '@/api/generated/login-providers/clients/add-login-provider'
import { deleteLoginProvider } from '@/api/generated/login-providers/clients/delete-login-provider'
import { getLoginProviders } from '@/api/generated/login-providers/clients/get-login-providers'
import { testLoginProvider } from '@/api/generated/login-providers/clients/test-login-provider'
import { updateLoginProvider } from '@/api/generated/login-providers/clients/update-login-provider'
import type { LoginProviderForCreationDto } from '@/api/generated/models/types/login-provider-for-creation-dto'
import type { LoginProviderForTestDto } from '@/api/generated/models/types/login-provider-for-test-dto'
import type { LoginProviderForUpdateDto } from '@/api/generated/models/types/login-provider-for-update-dto'

import { LoginProviderKeys } from './login-providers.keys'

// The page shows a failed load, so a toast would say it twice.
export const loginProviderListOptions = queryOptions({
  queryKey: LoginProviderKeys.lists(),
  queryFn: ({ signal }) => getLoginProviders({ signal, options: { suppressErrorToast: true } }),
})

export function useLoginProviders(enabled: boolean) {
  return useQuery({ ...loginProviderListOptions, enabled })
}

function useRefreshedList() {
  const queryClient = useQueryClient()
  return () => queryClient.invalidateQueries({ queryKey: LoginProviderKeys.lists() })
}

export function useAddLoginProvider() {
  const refresh = useRefreshedList()
  return useMutation({
    mutationFn: (body: LoginProviderForCreationDto) => addLoginProvider({ body, options: { suppressErrorToast: true } }),
    // The Server can save, then answer 500 when the reload of the login modes fails, so the list reads again after each answer.
    onSettled: refresh,
  })
}

export function useUpdateLoginProvider(id: string) {
  const refresh = useRefreshedList()
  return useMutation({
    mutationFn: (body: LoginProviderForUpdateDto) =>
      updateLoginProvider({ path: { id }, body, options: { suppressErrorToast: true } }),
    onSettled: refresh,
  })
}

/** The card shows a refusal, such as the 409 of the last Admin who can sign in. */
export function useDeleteLoginProvider(id: string) {
  const refresh = useRefreshedList()
  return useMutation({
    mutationFn: () => deleteLoginProvider({ path: { id }, options: { suppressErrorToast: true } }),
    onSettled: refresh,
  })
}

/** Runs discovery with the values of the form, and saves nothing (IDP-7). */
export function useTestLoginProvider() {
  return useMutation({
    mutationFn: (body: LoginProviderForTestDto) => testLoginProvider({ body, options: { suppressErrorToast: true } }),
  })
}
