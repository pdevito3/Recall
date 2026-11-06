import { InfiniteQueryObserver, QueryClient } from '@tanstack/react-query'
import { expect, test, vi } from 'vitest'

import type { SessionSummaryDto } from '@/api/generated/models/types/session-summary-dto'

import { SessionKeys } from './session.keys'
import { patchTimelineRow, withFirstPageOnly, withRowPatched, withRowRemoved, type TimelineData } from './timeline-cache'

const row = (id: string): SessionSummaryDto =>
  ({ id, preview: 'old' }) as SessionSummaryDto

const data: TimelineData = {
  pages: [
    { items: [row('a')], nextCursor: 'c1' },
    { items: [row('b')], nextCursor: null },
  ],
  pageParams: [undefined, 'c1'],
}

test('a patch changes one row and keeps the pages', () => {
  const patched = withRowPatched(data, 'b', { preview: 'new' })

  expect(patched.pages[1].items[0].preview).toBe('new')
  expect(patched.pages[0]).toBe(data.pages[0])
})

test('a removal keeps the other rows and the cursor of the page', () => {
  const removed = withRowRemoved(data, 'a')

  expect(removed.pages[0]).toEqual({ items: [], nextCursor: 'c1' })
  expect(removed.pages[1]).toBe(data.pages[1])
})

test('the head keeps the first page and its cursor', () => {
  expect(withFirstPageOnly(data)).toEqual({ pages: [data.pages[0]], pageParams: [undefined] })
})

test('a row that changes during a read reads again, so the read does not put the old row back', async () => {
  const onePage = (preview: string) => ({ items: [{ ...row('a'), preview }], nextCursor: null })
  type OnePage = ReturnType<typeof onePage>
  let answerFirstRead: (page: OnePage) => void = () => undefined
  const firstRead = new Promise<OnePage>((resolve) => {
    answerFirstRead = resolve
  })
  const read = vi
    .fn<() => Promise<OnePage>>()
    .mockReturnValueOnce(firstRead)
    .mockResolvedValueOnce(onePage('saved words'))
  const client = new QueryClient()
  const observer = new InfiniteQueryObserver(client, {
    queryKey: SessionKeys.timeline(),
    queryFn: read,
    initialPageParam: undefined,
    getNextPageParam: () => undefined,
  })
  const stop = observer.subscribe(() => undefined)

  await vi.waitFor(() => expect(read).toHaveBeenCalledTimes(1))
  client.setQueryData<TimelineData>(SessionKeys.timeline(), { pages: [onePage('')], pageParams: [undefined] })
  patchTimelineRow(client, 'a', { preview: 'saved words' })
  answerFirstRead(onePage(''))

  await vi.waitFor(() => expect(read).toHaveBeenCalledTimes(2))
  await vi.waitFor(() => expect(client.isFetching()).toBe(0))
  expect(client.getQueryData<TimelineData>(SessionKeys.timeline())?.pages[0].items[0].preview).toBe('saved words')
  stop()
})
