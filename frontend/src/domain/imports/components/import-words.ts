import { parseDate } from '@internationalized/date'

import { browserTimeZone, formatDate, formatInstant, parseTimeZone, timeZoneLabel } from '@/lib/zoned-time'

const numbers = new Intl.NumberFormat('en-US')

export const countOf = (count: number) => numbers.format(count)

export const entriesWord = (count: number) => `${countOf(count)} ${count === 1 ? 'entry' : 'entries'}`

/** The word that agrees with a count: `agree(1, 'goes', 'go')` is "goes". */
export const agree = (count: number, one: string, many: string) => (count === 1 ? one : many)

export const daysWord = (count: number) => `${countOf(count)} ${count === 1 ? 'day' : 'days'}`

export const dayOf = (date: string) => formatDate(parseDate(date), { weekday: 'long', year: 'numeric', month: 'long', day: 'numeric' })

const shortDayOf = (date: string) => formatDate(parseDate(date), { year: 'numeric', month: 'short', day: 'numeric' })

/** The dates of a page of days, for example "Mar 1, 2024 to Mar 4, 2024", or one date for a page of one day. */
export const dateSpanOf = (from: string, to: string) => (from === to ? shortDayOf(from) : `${shortDayOf(from)} to ${shortDayOf(to)}`)

export const zoneLabelOf = (zone: string) => {
  try {
    return timeZoneLabel(parseTimeZone(zone))
  } catch {
    return zone
  }
}

/** A moment in the zone of the browser, as "Nov 26, 2026, 4:14 PM", with no line break in it, so it wraps as one unit. */
export const momentOf = (instant: string) =>
  formatInstant(new Date(instant), browserTimeZone(), { year: 'numeric', month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit' }).replace(/\s/g, '\u00a0')
