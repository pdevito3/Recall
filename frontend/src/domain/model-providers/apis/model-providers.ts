import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'

import { addModelProvider } from '@/api/generated/model-providers/clients/add-model-provider'
import { deleteModelProvider } from '@/api/generated/model-providers/clients/delete-model-provider'
import { getActiveModelProvider } from '@/api/generated/model-providers/clients/get-active-model-provider'
import { getModelProviders } from '@/api/generated/model-providers/clients/get-model-providers'
import { testModelProvider } from '@/api/generated/model-providers/clients/test-model-provider'
import { updateModelProvider } from '@/api/generated/model-providers/clients/update-model-provider'
import type { ModelProviderForCreationDto } from '@/api/generated/models/types/model-provider-for-creation-dto'
import type { ModelProviderForUpdateDto } from '@/api/generated/models/types/model-provider-for-update-dto'

export const ModelProviderKeys = {
  all: ['model-providers'] as const,
  lists: () => [...ModelProviderKeys.all, 'list'] as const,
  active: () => [...ModelProviderKeys.all, 'active'] as const,
}

export function useModelProviders(enabled: boolean) {
  return useQuery({
    queryKey: ModelProviderKeys.lists(),
    queryFn: ({ signal }) => getModelProviders({ signal, options: { suppressErrorToast: true } }),
    enabled,
  })
}

export function useActiveModelProvider(pollMs: number | false = false) {
  return useQuery({
    queryKey: ModelProviderKeys.active(),
    queryFn: ({ signal }) => getActiveModelProvider({ signal }),
    refetchInterval: pollMs,
  })
}

function useRefreshedList() {
  const queryClient = useQueryClient()
  return () => queryClient.invalidateQueries({ queryKey: ModelProviderKeys.all })
}

export function useAddModelProvider() {
  const refresh = useRefreshedList()
  return useMutation({
    mutationFn: (body: ModelProviderForCreationDto) => addModelProvider({ body, options: { suppressErrorToast: true } }),
    onSuccess: refresh,
  })
}

export function useUpdateModelProvider(id: string) {
  const refresh = useRefreshedList()
  return useMutation({
    mutationFn: (body: ModelProviderForUpdateDto) => updateModelProvider({ path: { id }, body }),
    onSettled: refresh,
  })
}

export function useDeleteModelProvider(id: string) {
  const refresh = useRefreshedList()
  return useMutation({
    mutationFn: () => deleteModelProvider({ path: { id } }),
    onSettled: refresh,
  })
}

export function useTestModelProvider(id: string) {
  return useMutation({
    mutationFn: () => testModelProvider({ path: { id } }),
  })
}
