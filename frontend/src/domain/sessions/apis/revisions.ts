import { useInfiniteQuery, useQuery } from '@tanstack/react-query'

import { getCleanedRevision } from '@/api/generated/sessions/clients/get-cleaned-revision'
import { getCleanedRevisionPage } from '@/api/generated/sessions/clients/get-cleaned-revision-page'
import { getRawRevision } from '@/api/generated/sessions/clients/get-raw-revision'
import { getRawRevisionPage } from '@/api/generated/sessions/clients/get-raw-revision-page'
import { retryUnlessRefused } from '@/lib/problem-details'

import { SessionKeys, type DraftChannel } from './session.keys'

const clients = {
  raw: { page: getRawRevisionPage, one: getRawRevision },
  cleaned: { page: getCleanedRevisionPage, one: getCleanedRevision },
}

export function useRevisionPages(id: string, channel: DraftChannel, { enabled }: { enabled: boolean }) {
  return useInfiniteQuery({
    enabled,
    queryKey: SessionKeys.revisions(id, channel),
    queryFn: ({ pageParam, signal }) =>
      clients[channel].page({
        path: { id },
        query: { cursor: pageParam },
        signal,
        options: { suppressErrorToast: true },
      }),
    initialPageParam: undefined as string | undefined,
    getNextPageParam: (page) => page.nextCursor ?? undefined,
    retry: retryUnlessRefused,
  })
}

export function useRevision(id: string, channel: DraftChannel, number: number) {
  return useQuery({
    queryKey: SessionKeys.revision(id, channel, number),
    queryFn: ({ signal }) =>
      clients[channel].one({ path: { id, number }, signal, options: { suppressErrorToast: true } }),
    staleTime: Infinity,
    retry: retryUnlessRefused,
  })
}
