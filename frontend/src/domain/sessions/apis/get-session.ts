import { queryOptions, useQuery } from '@tanstack/react-query'

import { getSession } from '@/api/generated/sessions/clients/get-session'
import { retryUnlessRefused } from '@/lib/problem-details'

import { SessionKeys } from './session.keys'

const otherTabCheckMs = 30_000
export const pollWhileRunningMs = 2_000

export const sessionQueryOptions = (id: string) =>
  queryOptions({
    queryKey: SessionKeys.detail(id),
    queryFn: ({ signal }) => getSession({ path: { id }, signal, options: { suppressErrorToast: true } }),
    retry: retryUnlessRefused,
  })

export function useSession(id: string) {
  return useQuery({
    ...sessionQueryOptions(id),
    refetchOnWindowFocus: true,
    // A Cleanup runs on the server, and the page shows its result when it ends.
    refetchInterval: (query) => (query.state.data?.cleanupStatus === 'Running' ? pollWhileRunningMs : otherTabCheckMs),
  })
}
