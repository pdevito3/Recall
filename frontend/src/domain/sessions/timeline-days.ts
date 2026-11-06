import type { CalendarDate } from '@internationalized/date'

import type { SessionSummaryDto } from '@/api/generated/models/types/session-summary-dto'
import { formatDate } from '@/lib/zoned-time'

export interface DayGroup {
  day: string
  rows: SessionSummaryDto[]
}

export function groupByDay(rows: SessionSummaryDto[]): DayGroup[] {
  const groups: DayGroup[] = []
  for (const row of rows) {
    const last = groups.at(-1)
    if (last?.day === row.journalingDay) last.rows.push(row)
    else groups.push({ day: row.journalingDay, rows: [row] })
  }
  return groups
}

export function dayHeading(day: CalendarDate, today: CalendarDate): string {
  if (day.compare(today) === 0) return 'Today'
  if (day.compare(today.subtract({ days: 1 })) === 0) return 'Yesterday'
  const year = day.year === today.year ? undefined : 'numeric'
  return formatDate(day, { weekday: 'long', month: 'long', day: 'numeric', year })
}
