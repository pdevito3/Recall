import type { ImportEntryDto } from '@/api/generated/models/types/import-entry-dto'
import { formatInstant, parseTimeZone, timeZoneLabel, type TimeZone } from '@/lib/zoned-time'

export interface EntryTime {
  /** The start time of the entry, for example "8:30 AM". */
  time: string
  /** The zone next to the time, or null when the time is in the zone that the header of the batch names. */
  zoneLabel: string | null
  /** The zone of a typed time: the Server reads it in the zone of the entry. */
  editZone: string
}

const utc = 'UTC' as TimeZone

const clockOf = (hhmm: string) => {
  const [hour, minute] = hhmm.split(':').map(Number)
  return formatInstant(new Date(Date.UTC(2000, 0, 1, hour, minute)), utc, { hour: 'numeric', minute: '2-digit' })
}

/**
 * The start of an entry in its zone (IMP-36). A browser that does not know the zone cannot place the start, so the
 * entry shows the time that the person or the agent gave, with the ID of the zone. With no such time, it shows the start
 * in UTC and names UTC, so the time and its label always agree.
 */
export function entryTimeOf(entry: ImportEntryDto, batchZone: string): EntryTime {
  const id = entry.timeZone ?? batchZone
  let zone: TimeZone
  try {
    zone = parseTimeZone(id)
  } catch {
    const given = entry.review.time ?? entry.time
    return given
      ? { time: clockOf(given), zoneLabel: id, editZone: id }
      : { time: formatInstant(new Date(entry.startsAt), utc, { hour: 'numeric', minute: '2-digit' }), zoneLabel: 'UTC', editZone: id }
  }
  return {
    time: formatInstant(new Date(entry.startsAt), zone, { hour: 'numeric', minute: '2-digit' }),
    zoneLabel: entry.timeZone && entry.timeZone !== batchZone ? timeZoneLabel(zone) : null,
    editZone: timeZoneLabel(zone),
  }
}
