import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'

import type { PersonalAccessTokenForCreationDto } from '@/api/generated/models/types/personal-access-token-for-creation-dto'
import { addPersonalAccessToken } from '@/api/generated/personal-access-tokens/clients/add-personal-access-token'
import { getPersonalAccessTokens } from '@/api/generated/personal-access-tokens/clients/get-personal-access-tokens'
import { getUserPersonalAccessTokens } from '@/api/generated/personal-access-tokens/clients/get-user-personal-access-tokens'
import { revokePersonalAccessToken } from '@/api/generated/personal-access-tokens/clients/revoke-personal-access-token'
import { retryUnlessRefused } from '@/lib/problem-details'

import { PersonalAccessTokenKeys } from './personal-access-token.keys'

export function useMyPersonalAccessTokens() {
  return useQuery({
    queryKey: PersonalAccessTokenKeys.mine(),
    queryFn: ({ signal }) => getPersonalAccessTokens({ signal, options: { suppressErrorToast: true } }),
    retry: retryUnlessRefused,
  })
}

export function useUserPersonalAccessTokens(userId: string) {
  return useQuery({
    queryKey: PersonalAccessTokenKeys.ofUser(userId),
    queryFn: ({ signal }) => getUserPersonalAccessTokens({ path: { id: userId }, signal, options: { suppressErrorToast: true } }),
    retry: retryUnlessRefused,
  })
}

/**
 * The answer holds the value of the token. Only this mutation result keeps it. The dialog resets the mutation when it
 * closes, and gcTime 0 then drops it from the mutation cache, so no cache or storage holds it after that.
 */
export function useAddPersonalAccessToken() {
  const queryClient = useQueryClient()
  return useMutation({
    gcTime: 0,
    mutationFn: (body: PersonalAccessTokenForCreationDto) =>
      addPersonalAccessToken({ body, options: { suppressErrorToast: true } }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: PersonalAccessTokenKeys.lists() }),
  })
}

export function useRevokePersonalAccessToken() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (id: string) => revokePersonalAccessToken({ path: { id } }),
    onSettled: () => queryClient.invalidateQueries({ queryKey: PersonalAccessTokenKeys.lists() }),
  })
}
