import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'

import { generateSummary } from '@/api/generated/summaries/clients/generate-summary'
import { getSummary } from '@/api/generated/summaries/clients/get-summary'

import type { SummaryParams } from './summary.keys'
import { SummaryKeys } from './summary.keys'

export const pollWhileGeneratingMs = 2_000

/**
 * The Summary of the period that holds the date. The first read opens the period: the generate call starts a run for a
 * Summary that is missing, stale, or failed. Each later read is a GET, because a POST after a failed run would start a
 * new run with no press of the person. gcTime 0 forgets the period when the page leaves it, so a new visit opens it again.
 */
export function useSummary(params: SummaryParams) {
  return useQuery({
    queryKey: SummaryKeys.detail(params),
    queryFn: ({ client, queryKey, signal }) =>
      client.getQueryData(queryKey) === undefined
        ? // No signal: the open must not end when React mounts the page twice, or the page sends a second POST.
          generateSummary({ path: params, options: { suppressErrorToast: true } })
        : getSummary({ path: params, signal, options: { suppressErrorToast: true } }),
    refetchInterval: (query) => (query.state.data?.status === 'Generating' ? pollWhileGeneratingMs : false),
    placeholderData: keepPreviousData,
    gcTime: 0,
  })
}

/** Retry and Regenerate: the answer is Generating, so the Summary query polls again. */
export function useGenerateSummary(params: SummaryParams) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: () => generateSummary({ path: params }),
    onMutate: () => queryClient.cancelQueries({ queryKey: SummaryKeys.detail(params) }),
    onSuccess: (summary) => queryClient.setQueryData(SummaryKeys.detail(params), summary),
    onError: () => queryClient.invalidateQueries({ queryKey: SummaryKeys.detail(params) }),
  })
}
