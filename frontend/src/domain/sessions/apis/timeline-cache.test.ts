import { expect, test } from 'vitest'

import type { SessionSummaryDto } from '@/api/generated/models/types/session-summary-dto'

import { withFirstPageOnly, withRowPatched, withRowRemoved, type TimelineData } from './timeline-cache'

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
