import type { InfiniteData, QueryClient } from '@tanstack/react-query'

import type { SessionPageDto } from '@/api/generated/models/types/session-page-dto'
import type { SessionSummaryDto } from '@/api/generated/models/types/session-summary-dto'
import { openChannel } from '@/lib/browser-channel'

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

function changeTimeline(queryClient: QueryClient, change: (data: TimelineData) => TimelineData) {
  const key = SessionKeys.timeline()
  const wasWaitingForARead = queryClient.getQueryState(key)?.isInvalidated ?? false
  queryClient.setQueryData<TimelineData>(key, (data) => data && change(data))
  // A write clears the mark that a read is due, and a read that left before this change would put the old row back.
  if (wasWaitingForARead || queryClient.isFetching({ queryKey: key }) > 0) {
    void queryClient.invalidateQueries({ queryKey: key })
  }
}

export function patchTimelineRow(queryClient: QueryClient, id: string, patch: Partial<SessionSummaryDto>) {
  changeTimeline(queryClient, (data) => withRowPatched(data, id, patch))
}

export function removeTimelineRow(queryClient: QueryClient, id: string) {
  changeTimeline(queryClient, (data) => withRowRemoved(data, id))
}

/** A new entry sorts first. A refetch of each page of a long list costs one request for each page, so only the first page reads again. */
export function refreshTimelineHead(queryClient: QueryClient) {
  queryClient.setQueryData<TimelineData>(SessionKeys.timeline(), (data) => data && withFirstPageOnly(data))
  return queryClient.invalidateQueries({ queryKey: SessionKeys.timeline() })
}

let otherTabs: BroadcastChannel | null | undefined

/** Opens at the first use, so a page that shows no timeline and gets no words opens no channel. */
const channelToOtherTabs = () => (otherTabs === undefined ? (otherTabs = openChannel('recall.timeline')) : otherTabs)

/** Only the tab that follows a recording reads its end, so it tells the other tabs of this browser that its words came. */
export function tellOtherTabsOfNewWords() {
  channelToOtherTabs()?.postMessage('words')
}

/** Reads the first page of the timeline again when another tab tells of new words. */
export function followNewWordsOfOtherTabs(queryClient: QueryClient): () => void {
  const channel = channelToOtherTabs()
  const refresh = () => void refreshTimelineHead(queryClient)
  channel?.addEventListener('message', refresh)
  return () => channel?.removeEventListener('message', refresh)
}
