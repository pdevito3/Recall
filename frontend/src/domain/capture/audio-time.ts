import { formatInstant, type TimeZone } from '@/lib/zoned-time'

/** A length of audio as m:ss. */
export const clock = (ms: number) => {
  const seconds = Math.floor(ms / 1000)
  return `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, '0')}`
}

/** The time of day that a recording started, as the timeline shows it. */
export const timeOf = (startedAt: string, zone: TimeZone) => formatInstant(new Date(startedAt), zone, { hour: 'numeric', minute: '2-digit' })
