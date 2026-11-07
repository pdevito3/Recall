import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'

import type { PersonProposalResponseDto } from '@/api/generated/models/types/person-proposal-response-dto'
import type { SessionDto } from '@/api/generated/models/types/session-dto'
import type { SuggestionResponseDto } from '@/api/generated/models/types/suggestion-response-dto'
import type { WaitingCleanupApplyDto } from '@/api/generated/models/types/waiting-cleanup-apply-dto'
import { acceptSuggestion } from '@/api/generated/sessions/clients/accept-suggestion'
import { applyWaitingCleanup } from '@/api/generated/sessions/clients/apply-waiting-cleanup'
import { getSession } from '@/api/generated/sessions/clients/get-session'
import { getSessionPage } from '@/api/generated/sessions/clients/get-session-page'
import { rejectSuggestion } from '@/api/generated/sessions/clients/reject-suggestion'
import { respondToPersonProposal } from '@/api/generated/sessions/clients/respond-to-person-proposal'
import { startCleanup } from '@/api/generated/sessions/clients/start-cleanup'
import { stopCleanup } from '@/api/generated/sessions/clients/stop-cleanup'

export const pollWhileRunningMs = 2_000
const sessionsShown = 10

const CleanupKeys = {
  all: ['cleanup-sessions'] as const,
  recent: () => [...CleanupKeys.all, 'recent'] as const,
  session: (id: string) => [...CleanupKeys.all, 'session', id] as const,
}

export function useRecentSessions() {
  return useQuery({
    queryKey: CleanupKeys.recent(),
    queryFn: ({ signal }) => getSessionPage({ query: { pageSize: sessionsShown }, signal, options: { suppressErrorToast: true } }),
  })
}

export function useCleanupSession(id: string) {
  return useQuery({
    queryKey: CleanupKeys.session(id),
    queryFn: ({ signal }) => getSession({ path: { id }, signal, options: { suppressErrorToast: true } }),
    refetchInterval: (query) => (query.state.data?.cleanupStatus === 'Running' ? pollWhileRunningMs : false),
  })
}

function usePutSession(id: string) {
  const queryClient = useQueryClient()
  return (session: SessionDto) => queryClient.setQueryData(CleanupKeys.session(id), session)
}

export function useStartCleanup(id: string) {
  const put = usePutSession(id)
  return useMutation({
    mutationFn: () => startCleanup({ path: { id }, options: { suppressErrorToast: true } }),
    onSuccess: put,
  })
}

export function useStopCleanup(id: string) {
  const put = usePutSession(id)
  return useMutation({
    mutationFn: () => stopCleanup({ path: { id }, options: { suppressErrorToast: true } }),
    onSuccess: put,
  })
}

/** A refusal means that Cleaned moved or the result no longer waits, so the card reads the Session again. */
export function useApplyWaitingCleanup(id: string) {
  const put = usePutSession(id)
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (body: WaitingCleanupApplyDto) => applyWaitingCleanup({ path: { id }, body, options: { suppressErrorToast: true } }),
    onSuccess: put,
    onError: () => queryClient.invalidateQueries({ queryKey: CleanupKeys.session(id) }),
  })
}

export function useRespondToSuggestion(id: string) {
  const put = usePutSession(id)
  return useMutation({
    mutationFn: ({ accept, ...body }: SuggestionResponseDto & { accept: boolean }) =>
      accept ? acceptSuggestion({ path: { id }, body }) : rejectSuggestion({ path: { id }, body }),
    onSuccess: put,
  })
}

export function useRespondToProposal(id: string) {
  const put = usePutSession(id)
  return useMutation({
    mutationFn: (body: PersonProposalResponseDto) => respondToPersonProposal({ path: { id }, body, options: { suppressErrorToast: true } }),
    onSuccess: put,
  })
}
