import type { InfiniteData, QueryClient } from '@tanstack/react-query'

import type { SessionPageDto } from '@/api/generated/models/types/session-page-dto'
import type { SessionSummaryDto } from '@/api/generated/models/types/session-summary-dto'

import { SessionKeys } from './session.keys'

export type TimelineData = InfiniteData<SessionPageDto, string | undefined>

export function withRowPatched(data: TimelineData, id: string, patch: Partial<SessionSummaryDto>): TimelineData {
  return {
    ...data,
    pages: data.pages.map((page) =>
      page.items.some((row) => row.id === id)
        ? { ...page, items: page.items.map((row) => (row.id === id ? { ...row, ...patch } : row)) }
        : page,
    ),
  }
}

export function withRowRemoved(data: TimelineData, id: string): TimelineData {
  return {
    ...data,
    pages: data.pages.map((page) =>
      page.items.some((row) => row.id === id) ? { ...page, items: page.items.filter((row) => row.id !== id) } : page,
    ),
  }
}

export function withFirstPageOnly(data: TimelineData): TimelineData {
  return { pages: data.pages.slice(0, 1), pageParams: data.pageParams.slice(0, 1) }
}

/** Changes one row where it is, so the list keeps its pages and its scroll position with no read of each page. */
export function patchTimelineRow(queryClient: QueryClient, id: string, patch: Partial<SessionSummaryDto>) {
  queryClient.setQueryData<TimelineData>(SessionKeys.timeline(), (data) => data && withRowPatched(data, id, patch))
  // A read that left before this change would put the old row back, so it reads again.
  if (queryClient.isFetching({ queryKey: SessionKeys.timeline() }) > 0) {
    void queryClient.invalidateQueries({ queryKey: SessionKeys.timeline() })
  }
}

export function removeTimelineRow(queryClient: QueryClient, id: string) {
  queryClient.setQueryData<TimelineData>(SessionKeys.timeline(), (data) => data && withRowRemoved(data, id))
}

/** A new entry sorts first. A refetch of each page of a long list costs one request for each page, so only the first page reads again. */
export function refreshTimelineHead(queryClient: QueryClient) {
  queryClient.setQueryData<TimelineData>(SessionKeys.timeline(), (data) => data && withFirstPageOnly(data))
  return queryClient.invalidateQueries({ queryKey: SessionKeys.timeline() })
}
