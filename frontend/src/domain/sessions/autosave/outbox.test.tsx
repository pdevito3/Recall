import { afterEach, expect, test, vi } from 'vitest'

import { readOutbox, readOutboxEntry, removeFromOutbox, writeToOutbox, type OutboxEntry } from './outbox'

const entryWith = (words: string, key = `${words}.raw`): OutboxEntry => ({
  key,
  channel: 'raw',
  clientSessionId: words,
  startedAt: '2026-11-06T15:30:00.000Z',
  sessionId: null,
  baseRevision: 0,
  latestRevision: 0,
  change: { value: words, savedAt: '2026-11-06T15:31:00.000Z' },
})

afterEach(() => {
  localStorage.clear()
})

test('the outbox of one person shows nothing to another person', () => {
  writeToOutbox('person-a', entryWith('words of a'))

  expect(readOutbox('person-b')).toEqual([])
  expect(readOutboxEntry('person-b', 'words of a.raw')).toBeNull()
  removeFromOutbox('person-b', 'words of a.raw')
  expect(readOutbox('person-a').map((entry) => entry.change.value)).toEqual(['words of a'])
})

test('an owner whose name starts with the name of another owner has its own outbox', () => {
  writeToOutbox('ana.reyes', entryWith('words of ana reyes'))

  expect(readOutbox('ana')).toEqual([])
  expect(readOutbox('ana.reyes').map((entry) => entry.change.value)).toEqual(['words of ana reyes'])
})

for (const [name, stored] of [
  ['text that is not JSON', '{not json'],
  ['null', 'null'],
  ['a number', '42'],
  ['an entry with no change', JSON.stringify({ ...entryWith('x'), change: undefined })],
  ['an unknown entry with no words', JSON.stringify({ ...entryWith('x'), channel: 'other', change: { value: '', savedAt: '2026-11-06T15:31:00.000Z' } })],
] as const) {
  test(`${name} does not hide the other words, and it goes from storage`, () => {
    writeToOutbox('person-a', entryWith('good words'))
    localStorage.setItem('recall.unsent.person-a.broken', stored)

    expect(readOutbox('person-a').map((entry) => entry.change.value)).toEqual(['good words'])
    expect(readOutboxEntry('person-a', 'broken')).toBeNull()
    expect(localStorage.getItem('recall.unsent.person-a.broken')).toBeNull()
  })
}

test('an entry that this version cannot read keeps its words as a new entry', () => {
  localStorage.setItem('recall.unsent.person-a.unknown', JSON.stringify({ ...entryWith('words of an old version'), channel: 'other' }))

  const [entry] = readOutbox('person-a')

  expect(entry).toMatchObject({ channel: 'raw', sessionId: null, change: { value: 'words of an old version' } })
  expect(readOutboxEntry('person-a', entry!.key)?.change.value).toBe('words of an old version')
  expect(localStorage.getItem('recall.unsent.person-a.unknown')).toBeNull()
})

test('an entry that this version cannot read stays in storage while the browser has no room for the new entry', () => {
  const stored = JSON.stringify({ ...entryWith('words of an old version'), channel: 'other' })
  localStorage.setItem('recall.unsent.person-a.unknown', stored)
  const setItem = vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
    throw new DOMException('full', 'QuotaExceededError')
  })

  expect(readOutbox('person-a')).toEqual([])

  setItem.mockRestore()
  expect(localStorage.getItem('recall.unsent.person-a.unknown')).toBe(stored)
})
