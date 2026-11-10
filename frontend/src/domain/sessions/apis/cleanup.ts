import { skipToken, useMutation, useQuery, useQueryClient, type QueryClient } from '@tanstack/react-query'

import type { PersonProposalResponseDto } from '@/api/generated/models/types/person-proposal-response-dto'
import type { SessionDto } from '@/api/generated/models/types/session-dto'
import type { SuggestionResponseDto } from '@/api/generated/models/types/suggestion-response-dto'
import type { WaitingCleanupApplyDto } from '@/api/generated/models/types/waiting-cleanup-apply-dto'
import { acceptSuggestion } from '@/api/generated/sessions/clients/accept-suggestion'
import { applyWaitingCleanup } from '@/api/generated/sessions/clients/apply-waiting-cleanup'
import { getCleanedRevision } from '@/api/generated/sessions/clients/get-cleaned-revision'
import { rejectSuggestion } from '@/api/generated/sessions/clients/reject-suggestion'
import { respondToPersonProposal } from '@/api/generated/sessions/clients/respond-to-person-proposal'
import { startCleanup } from '@/api/generated/sessions/clients/start-cleanup'
import { stopCleanup } from '@/api/generated/sessions/clients/stop-cleanup'
import { PersonKeys } from '@/domain/people/apis/person.keys'

import { SessionKeys } from './session.keys'
import { patchTimelineRow } from './timeline-cache'

/** Each reply of a Cleanup route is the whole Session, so the entry page, its editor, and its tags read it at once. */
function putReply(queryClient: QueryClient, session: SessionDto) {
  // A read that started before this reply holds the old row, and it must not answer over the reply.
  void queryClient.cancelQueries({ queryKey: SessionKeys.detail(session.id), exact: true })
  queryClient.setQueryData(SessionKeys.detail(session.id), session)
  const { cleanupStatus, cleanupFailure, synopsis, moods, topics, personIds, version, metadataVersion } = session
  patchTimelineRow(queryClient, session.id, { cleanupStatus, cleanupFailure, synopsis, moods, topics, personIds, version, metadataVersion })
}

function usePutReply() {
  const queryClient = useQueryClient()
  return (session: SessionDto) => putReply(queryClient, session)
}

export function useStartCleanup(id: string) {
  const put = usePutReply()
  return useMutation({
    mutationFn: () => startCleanup({ path: { id }, options: { suppressErrorToast: true } }),
    onSuccess: put,
  })
}

export function useStopCleanup(id: string) {
  const put = usePutReply()
  return useMutation({
    mutationFn: () => stopCleanup({ path: { id }, options: { suppressErrorToast: true } }),
    onSuccess: put,
  })
}

export function useWaitingCleanupText(id: string, revision: number | null) {
  return useQuery({
    queryKey: SessionKeys.revision(id, 'cleaned', revision ?? 0),
    queryFn:
      revision === null
        ? skipToken
        : ({ signal }) => getCleanedRevision({ path: { id, number: revision }, signal, options: { suppressErrorToast: true } }),
    staleTime: Infinity,
    select: (waiting) => waiting.text,
  })
}

/** A refusal means that Cleaned moved, the result no longer waits, or a new Cleanup runs, so the page reads the Session again. */
export function useApplyWaitingCleanup(id: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (body: WaitingCleanupApplyDto) => applyWaitingCleanup({ path: { id }, body, options: { suppressErrorToast: true } }),
    onSuccess: (session) => {
      putReply(queryClient, session)
      void queryClient.invalidateQueries({ queryKey: SessionKeys.revisions(id, 'cleaned') })
    },
    onError: () => queryClient.invalidateQueries({ queryKey: SessionKeys.detail(id), exact: true }),
  })
}

export function useRespondToSuggestion(id: string) {
  const put = usePutReply()
  return useMutation({
    mutationFn: ({ accept, ...body }: SuggestionResponseDto & { accept: boolean }) =>
      accept ? acceptSuggestion({ path: { id }, body }) : rejectSuggestion({ path: { id }, body }),
    onSuccess: put,
  })
}

/** An approval can add a Person to the directory, and it writes a Cleaned Revision. */
export function useRespondToProposal(id: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (body: PersonProposalResponseDto) => respondToPersonProposal({ path: { id }, body, options: { suppressErrorToast: true } }),
    onSuccess: (session) => {
      putReply(queryClient, session)
      void queryClient.invalidateQueries({ queryKey: PersonKeys.all })
      void queryClient.invalidateQueries({ queryKey: SessionKeys.revisions(id, 'cleaned') })
    },
  })
}
