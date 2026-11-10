import { useInfiniteQuery, useQueryClient } from '@tanstack/react-query'
import { useEffect } from 'react'

import { getSessionPage } from '@/api/generated/sessions/clients/get-session-page'
import { retryUnlessRefused } from '@/lib/problem-details'

import { SessionKeys } from './session.keys'
import { followNewWordsOfOtherTabs, type TimelineData } from './timeline-cache'

const timelinePageSize = 50
const freshForMs = 60_000

// A refetch reads each loaded page in turn. Deep in a long list, that is many requests, and the list can move while the person reads it.
const onlyWhenOnFirstPage = (query: { state: { data: TimelineData | undefined } }) =>
  (query.state.data?.pages.length ?? 0) <= 1

export function useSessionTimeline() {
  const queryClient = useQueryClient()
  useEffect(() => followNewWordsOfOtherTabs(queryClient), [queryClient])
  return useInfiniteQuery({
    queryKey: SessionKeys.timeline(),
    queryFn: ({ pageParam, signal }) =>
      getSessionPage({
        query: { cursor: pageParam, pageSize: timelinePageSize },
        signal,
        options: { suppressErrorToast: true },
      }),
    initialPageParam: undefined as string | undefined,
    getNextPageParam: (page) => page.nextCursor ?? undefined,
    staleTime: freshForMs,
    refetchOnMount: onlyWhenOnFirstPage,
    refetchOnWindowFocus: onlyWhenOnFirstPage,
    retry: retryUnlessRefused,
  })
}
