import { describe, expect, test } from 'vitest'

import { parseTimeZone } from '@/lib/zoned-time'

import { nextPeriodDate, paragraphsOf, periodTitle, previousPeriodDate, sourceCountText, writtenAtText } from './summary-words'

describe('periodTitle', () => {
  test.each([
    { period: 'Day', periodDate: '2026-11-07', periodLastDate: '2026-11-07', title: 'Sat, Nov 7, 2026' },
    { period: 'Week', periodDate: '2026-06-01', periodLastDate: '2026-06-07', title: 'Mon, Jun 1 - Sun, Jun 7, 2026' },
    { period: 'Week', periodDate: '2025-12-29', periodLastDate: '2026-01-04', title: 'Mon, Dec 29, 2025 - Sun, Jan 4, 2026' },
    { period: 'Month', periodDate: '2026-02-01', periodLastDate: '2026-02-28', title: 'February 2026' },
    { period: 'Quarter', periodDate: '2026-10-01', periodLastDate: '2026-12-31', title: 'Oct 1 - Dec 31, 2026' },
    { period: 'Year', periodDate: '2026-01-01', periodLastDate: '2026-12-31', title: '2026' },
  ] as const)('a $period from $periodDate reads "$title" in a zone west of UTC', ({ title, ...bounds }) => {
    expect(periodTitle(bounds)).toBe(title)
  })
})

describe('the periods next to a period', () => {
  test('a Week goes back to the Sunday before its Monday and on to the Monday after its Sunday', () => {
    const week = { period: 'Week', periodDate: '2026-06-01', periodLastDate: '2026-06-07' } as const

    expect(previousPeriodDate(week)).toBe('2026-05-31')
    expect(nextPeriodDate(week)).toBe('2026-06-08')
  })

  test('a Quarter crosses into the year before and the year after', () => {
    expect(previousPeriodDate({ period: 'Quarter', periodDate: '2026-01-01', periodLastDate: '2026-03-31' })).toBe('2025-12-31')
    expect(nextPeriodDate({ period: 'Quarter', periodDate: '2026-10-01', periodLastDate: '2026-12-31' })).toBe('2027-01-01')
  })

  test('a Day after the end of February of a leap year is the 29th', () => {
    expect(nextPeriodDate({ period: 'Day', periodDate: '2028-02-28', periodLastDate: '2028-02-28' })).toBe('2028-02-29')
  })
})

describe('sourceCountText', () => {
  test.each([
    { period: 'Day', count: 1, text: 'Reads 1 Session' },
    { period: 'Week', count: 3, text: 'Reads 3 Sessions' },
    { period: 'Month', count: 3, text: 'Reads 3 days' },
    { period: 'Quarter', count: 1, text: 'Reads 1 month' },
    { period: 'Year', count: 2, text: 'Reads 2 quarters' },
  ] as const)('a $period with $count sources reads "$text"', ({ period, count, text }) => {
    expect(sourceCountText(period, count)).toBe(text)
  })
})

test('the time of writing shows in the zone that it is given', () => {
  expect(writtenAtText('2026-11-07T20:04:00Z', parseTimeZone('America/Chicago'))).toBe('Written Nov 7, 2026, 2:04 PM')
})

test('a blank line starts a paragraph, and a single line break stays in its paragraph', () => {
  expect(paragraphsOf('One.\nStill one.\n\n  Two.  \n\n\n')).toEqual(['One.\nStill one.', 'Two.'])
})
