import { describe, expect, it } from 'vitest'

import type { ImportDayDto } from '@/api/generated/models/types/import-day-dto'

import { countsOf, pagesOf } from './pages-of'

const day = (date: string, entryCount: number, rejectedCount = 0, rejected = false): ImportDayDto => ({ date, entryCount, rejectedCount, rejected })

describe('pagesOf', () => {
  it('@IMP-35 puts whole days on a page up to the size, and starts the next page with the day that does not fit', () => {
    const days = [day('2024-03-01', 120), day('2024-03-02', 80), day('2024-03-03', 1), day('2024-03-04', 50)]

    expect(pagesOf(days).map(({ from, to }) => [from, to])).toEqual([
      ['2024-03-01', '2024-03-02'],
      ['2024-03-03', '2024-03-04'],
    ])
  })

  it('@IMP-35 gives a day of more entries than the size a page alone', () => {
    const days = [day('2024-03-01', 3), day('2024-03-02', 250), day('2024-03-03', 3)]

    expect(pagesOf(days).map((page) => page.days.map((d) => d.date))).toEqual([['2024-03-01'], ['2024-03-02'], ['2024-03-03']])
  })

  it('@IMP-35 keeps a rejected day with no entry on the page of its neighbours', () => {
    const days = [day('2024-03-01', 2), day('2024-03-02', 0, 0, true), day('2024-03-03', 2)]

    expect(pagesOf(days, 4)).toEqual([{ from: '2024-03-01', to: '2024-03-03', days }])
  })

  it('@IMP-35 puts a day of 0 entries on the page of a day of more entries than the size', () => {
    const days = [day('2024-03-01', 250), day('2024-03-02', 0, 0, true), day('2024-03-03', 3)]

    expect(pagesOf(days).map((page) => page.days.map((d) => d.date))).toEqual([['2024-03-01', '2024-03-02'], ['2024-03-03']])
  })

  it('@IMP-35 gives no page for a batch with no day', () => {
    expect(pagesOf([])).toEqual([])
  })
})

describe('countsOf', () => {
  it('@IMP-41 counts an entry as rejected by its own reject or by the reject of its day', () => {
    const days = [day('2024-03-01', 5, 2), day('2024-03-02', 4, 1, true), day('2024-03-03', 0, 0, true)]

    expect(countsOf(days)).toEqual({ entries: 9, kept: 3, rejected: 6 })
  })
})
