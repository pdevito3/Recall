import type { ImportDayDto } from '@/api/generated/models/types/import-day-dto'

export const entriesOnAPage = 200

export interface ImportPage {
  from: string
  to: string
  days: ImportDayDto[]
}

export function pagesOf(days: readonly ImportDayDto[], size = entriesOnAPage): ImportPage[] {
  const pages: ImportDayDto[][] = []
  let count = 0
  for (const day of days) {
    const page = pages[pages.length - 1] as ImportDayDto[] | undefined
    if (page && (day.entryCount === 0 || count + day.entryCount <= size)) {
      page.push(day)
      count += day.entryCount
    } else {
      pages.push([day])
      count = day.entryCount
    }
  }
  return pages.map((page) => ({ from: page[0].date, to: page[page.length - 1].date, days: page }))
}

export interface ImportCounts {
  entries: number
  kept: number
  rejected: number
}

export function countsOf(days: readonly ImportDayDto[]): ImportCounts {
  const entries = days.reduce((sum, day) => sum + day.entryCount, 0)
  const kept = days.reduce((sum, day) => sum + (day.rejected ? 0 : day.entryCount - day.rejectedCount), 0)
  return { entries, kept, rejected: entries - kept }
}
