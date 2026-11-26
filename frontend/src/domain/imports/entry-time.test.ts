import { describe, expect, it } from 'vitest'

import type { ImportEntryDto } from '@/api/generated/models/types/import-entry-dto'

import { entryTimeOf } from './entry-time'

const entry = (patch: Partial<ImportEntryDto> = {}): ImportEntryDto => ({
  clientSessionId: 'c1',
  date: '2024-03-01',
  position: 0,
  time: '08:30',
  timeZone: null,
  raw: 'Words.',
  document: '{}',
  moods: [],
  topics: [],
  review: { rejected: false, words: null, time: null },
  isRejected: false,
  startsAt: '2024-03-01T13:30:00Z',
  wordsChangedAfterEdit: false,
  timeChangedAfterEdit: false,
  ...patch,
})

describe('entryTimeOf', () => {
  it('@IMP-36 shows the start in the zone of the batch with no label, and names the zone for an edit', () => {
    expect(entryTimeOf(entry(), 'America/New_York')).toEqual({ time: '8:30 AM', zoneLabel: null, editZone: 'Eastern Time' })
  })

  it('@IMP-36 labels the zone of an entry that is not the zone of the batch', () => {
    const london = entry({ time: '21:00', timeZone: 'Europe/London', startsAt: '2024-03-02T21:00:00Z' })

    expect(entryTimeOf(london, 'America/New_York')).toEqual({ time: '9:00 PM', zoneLabel: 'United Kingdom Time', editZone: 'United Kingdom Time' })
  })

  it('@IMP-36 with a zone that the browser does not know, shows the time of the person, else of the agent, with the zone ID', () => {
    expect(entryTimeOf(entry(), 'Mars/Olympus')).toEqual({ time: '8:30 AM', zoneLabel: 'Mars/Olympus', editZone: 'Mars/Olympus' })
    expect(entryTimeOf(entry({ review: { rejected: false, words: null, time: '21:05' } }), 'Mars/Olympus').time).toBe('9:05 PM')
  })

  it('@IMP-36 with a zone that the browser does not know and no time given, shows the start in UTC and names UTC', () => {
    const placed = entry({ time: null, startsAt: '2024-03-01T12:01:00Z' })

    expect(entryTimeOf(placed, 'Mars/Olympus')).toEqual({ time: '12:01 PM', zoneLabel: 'UTC', editZone: 'Mars/Olympus' })
  })
})
