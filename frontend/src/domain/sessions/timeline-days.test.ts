import { parseDate } from '@internationalized/date'
import { expect, test } from 'vitest'

import type { SessionSummaryDto } from '@/api/generated/models/types/session-summary-dto'

import { dayHeading, groupByDay } from './timeline-days'

const today = parseDate('2026-11-06')

test('the two newest days have a name, and an older day has its date', () => {
  expect(dayHeading(today, today)).toBe('Today')
  expect(dayHeading(parseDate('2026-11-05'), today)).toBe('Yesterday')
  expect(dayHeading(parseDate('2026-11-01'), today)).toContain('November 1')
})

test('a day of another year names the year', () => {
  expect(dayHeading(parseDate('2025-11-01'), today)).toContain('2025')
})

test('rows of one day form one group, in the order of the list', () => {
  const row = (id: string, journalingDay: string) => ({ id, journalingDay }) as SessionSummaryDto

  const groups = groupByDay([row('a', '2026-11-06'), row('b', '2026-11-06'), row('c', '2026-11-05')])

  expect(groups.map((group) => [group.day, group.rows.map((r) => r.id)])).toEqual([
    ['2026-11-06', ['a', 'b']],
    ['2026-11-05', ['c']],
  ])
})

test('a Session that keeps the day of an earlier zone joins its day, and the days stay newest first', () => {
  const row = (id: string, journalingDay: string) => ({ id, journalingDay }) as SessionSummaryDto

  const groups = groupByDay([row('in-los-angeles', '2026-11-10'), row('in-tokyo', '2026-11-11'), row('older', '2026-11-10')])

  expect(groups.map((group) => [group.day, group.rows.map((r) => r.id)])).toEqual([
    ['2026-11-11', ['in-tokyo']],
    ['2026-11-10', ['in-los-angeles', 'older']],
  ])
})
