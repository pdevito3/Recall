import type { CalendarDate, DateDuration } from '@internationalized/date'
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
  Month: { noun: 'month', source: ['Day', 'Days'], title: (first) => formatDate(first, { month: 'long', year: 'numeric' }, locale) },
  Quarter: { noun: 'quarter', source: ['Month', 'Months'], title: (first, last) => range(first, last, false) },
  Year: { noun: 'year', source: ['Quarter', 'Quarters'], title: (first) => String(first.year) },
}

export function periodNoun(period: SummaryPeriod): string {
  return PERIOD_WORDS[period].noun
}

/** The dates of the period, as the Server gives them. For example "Mon, Jun 1 - Sun, Jun 7, 2026" for a Week. */
export function periodTitle({ period, periodDate, periodLastDate }: PeriodBounds): string {
  return PERIOD_WORDS[period].title(parseDate(periodDate), parseDate(periodLastDate))
}

/**
 * "Reads 3 Sessions" for a Day or a Week, and the source periods of a roll-up, such as "Reads 2 Quarters" for a Year. A Summary
 * that read nothing, such as a failed one, gets "3 Sessions in this period".
 */
export function sourceCountText(period: SummaryPeriod, count: number, reads: boolean): string {
  const [one, many] = PERIOD_WORDS[period].source
  const sources = `${count} ${count === 1 ? one : many}`
  return reads ? `Reads ${sources}` : `${sources} in this period`
}

/** The years that the Server takes in a date. */
const FIRST_YEAR = 1900
const LAST_YEAR = 9998

/**
 * A step of one period from any day of a period lands in the period next to it, and a day in a period is enough, because the
 * Server owns its bounds. So a step needs only the date that the page asked for, not the Summary that is still on its way.
 */
const PERIOD_STEP: Record<SummaryPeriod, DateDuration> = {
  Day: { days: 1 },
  Week: { weeks: 1 },
  Month: { months: 1 },
  Quarter: { months: 3 },
  Year: { years: 1 },
}

function inServerYears(date: CalendarDate): string | null {
  return date.year >= FIRST_YEAR && date.year <= LAST_YEAR ? date.toString() : null
}

/** A day in the period before the one that holds `date`, or null before the first year that the Server takes. */
export function previousPeriodDate(period: SummaryPeriod, date: string): string | null {
  return inServerYears(parseDate(date).subtract(PERIOD_STEP[period]))
}

/** A day in the period after the one that holds `date`, or null after the last year that the Server takes. */
export function nextPeriodDate(period: SummaryPeriod, date: string): string | null {
  return inServerYears(parseDate(date).add(PERIOD_STEP[period]))
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
