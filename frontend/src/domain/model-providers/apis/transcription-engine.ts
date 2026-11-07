import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'

import { chooseTranscriptionEngine } from '@/api/generated/model-providers/clients/choose-transcription-engine'
import { getTranscriptionEngine } from '@/api/generated/model-providers/clients/get-transcription-engine'
import type { TranscriptionEngineChoiceDto } from '@/api/generated/models/types/transcription-engine-choice-dto'

import { ModelProviderKeys } from './model-providers'

/** Under the model provider keys, because a change to a provider can change the engine, and each provider change invalidates them. */
const TranscriptionEngineKeys = {
  current: () => [...ModelProviderKeys.all, 'transcription-engine'] as const,
}

export function useTranscriptionEngine() {
  return useQuery({
    queryKey: TranscriptionEngineKeys.current(),
    queryFn: ({ signal }) => getTranscriptionEngine({ signal, options: { suppressErrorToast: true } }),
  })
}

export function useChooseTranscriptionEngine() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (body: TranscriptionEngineChoiceDto) => chooseTranscriptionEngine({ body, options: { suppressErrorToast: true } }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: TranscriptionEngineKeys.current() }),
  })
}
