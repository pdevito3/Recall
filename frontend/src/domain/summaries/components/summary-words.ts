import type { CalendarDate } from '@internationalized/date'
import { parseDate } from '@internationalized/date'

import type { SummaryDto } from '@/api/generated/models/types/summary-dto'
import { summaryDtoPeriodEnum } from '@/api/generated/models/types/summary-dto'
import type { TimeZone } from '@/lib/zoned-time'
import { formatDate, formatInstant } from '@/lib/zoned-time'

import type { SummaryPeriod } from '../types'

type PeriodBounds = Pick<SummaryDto, 'period' | 'periodDate' | 'periodLastDate'>

/** The periods in the order of the tabs, from the shortest. */
export const SUMMARY_PERIODS: readonly SummaryPeriod[] = Object.values(summaryDtoPeriodEnum)

const locale = 'en-US'

const dayWithYear = { month: 'short', day: 'numeric', year: 'numeric' } as const
const dayWithoutYear = { month: 'short', day: 'numeric' } as const

/** The start shows its year only when the end is in another year. A long period does not need the weekday of its bounds. */
function range(first: CalendarDate, last: CalendarDate, showWeekday: boolean): string {
  const weekday = showWeekday ? ({ weekday: 'short' } as const) : {}
  const start = { ...weekday, ...(first.year === last.year ? dayWithoutYear : dayWithYear) }
  return `${formatDate(first, start, locale)} - ${formatDate(last, { ...weekday, ...dayWithYear }, locale)}`
}

const withYear = { weekday: 'short', ...dayWithYear } as const

interface PeriodWords {
  noun: string
  /** What a Summary of the period reads, as one and as more than one. */
  source: readonly [string, string]
  title: (first: CalendarDate, last: CalendarDate) => string
}

const PERIOD_WORDS: Record<SummaryPeriod, PeriodWords> = {
  Day: { noun: 'day', source: ['Session', 'Sessions'], title: (first) => formatDate(first, withYear, locale) },
  Week: { noun: 'week', source: ['Session', 'Sessions'], title: (first, last) => range(first, last, true) },
  Month: { noun: 'month', source: ['day', 'days'], title: (first) => formatDate(first, { month: 'long', year: 'numeric' }, locale) },
  Quarter: { noun: 'quarter', source: ['month', 'months'], title: (first, last) => range(first, last, false) },
  Year: { noun: 'year', source: ['quarter', 'quarters'], title: (first) => String(first.year) },
}

export function periodNoun(period: SummaryPeriod): string {
  return PERIOD_WORDS[period].noun
}

/** The dates of the period, as the Server gives them. For example "Mon, Jun 1 - Sun, Jun 7, 2026" for a Week. */
export function periodTitle({ period, periodDate, periodLastDate }: PeriodBounds): string {
  return PERIOD_WORDS[period].title(parseDate(periodDate), parseDate(periodLastDate))
}

/** "Reads 3 Sessions" for a Day or a Week, and the source periods of a roll-up, such as "Reads 2 quarters" for a Year. */
export function sourceCountText(period: SummaryPeriod, count: number): string {
  const [one, many] = PERIOD_WORDS[period].source
  return `Reads ${count} ${count === 1 ? one : many}`
}

/** The last day of the period before. The Server owns the bounds of a period, so a day in it is enough. */
export function previousPeriodDate({ periodDate }: PeriodBounds): string {
  return parseDate(periodDate).subtract({ days: 1 }).toString()
}

/** The first day of the period after. */
export function nextPeriodDate({ periodLastDate }: PeriodBounds): string {
  return parseDate(periodLastDate).add({ days: 1 }).toString()
}

export function writtenAtText(generatedAt: string, zone: TimeZone): string {
  const at = formatInstant(new Date(generatedAt), zone, { month: 'short', day: 'numeric', year: 'numeric', hour: 'numeric', minute: '2-digit' }, locale)
  return `Written ${at}`
}

/** The content is plain prose. A blank line starts a paragraph. */
export function paragraphsOf(content: string): string[] {
  return content
    .split(/\n\s*\n/)
    .map((paragraph) => paragraph.trim())
    .filter((paragraph) => paragraph.length > 0)
}
