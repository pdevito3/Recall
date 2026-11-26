import { parseDate } from '@internationalized/date'

import { formatDate, parseTimeZone, timeZoneLabel } from '@/lib/zoned-time'

const numbers = new Intl.NumberFormat('en-US')

export const countOf = (count: number) => numbers.format(count)

export const entriesWord = (count: number) => `${countOf(count)} ${count === 1 ? 'entry' : 'entries'}`

export const daysWord = (count: number) => `${countOf(count)} ${count === 1 ? 'day' : 'days'}`

export const dayOf = (date: string) => formatDate(parseDate(date), { weekday: 'long', year: 'numeric', month: 'long', day: 'numeric' })

export const zoneLabelOf = (zone: string) => {
  try {
    return timeZoneLabel(parseTimeZone(zone))
  } catch {
    return zone
  }
}
