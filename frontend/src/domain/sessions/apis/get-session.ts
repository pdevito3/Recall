import { queryOptions, useQuery } from '@tanstack/react-query'

import { getSession } from '@/api/generated/sessions/clients/get-session'
import { retryUnlessRefused } from '@/lib/problem-details'

import { SessionKeys } from './session.keys'

const otherTabCheckMs = 30_000

export const sessionQueryOptions = (id: string) =>
  queryOptions({
    queryKey: SessionKeys.detail(id),
    // The page shows the failure, so a toast would say it twice.
    queryFn: ({ signal }) => getSession({ path: { id }, signal, options: { suppressErrorToast: true } }),
    retry: retryUnlessRefused,
  })

/** The check on focus and the timer show a save from another tab or device while the person reads. */
export function useSession(id: string) {
  return useQuery({ ...sessionQueryOptions(id), refetchOnWindowFocus: true, refetchInterval: otherTabCheckMs })
}
