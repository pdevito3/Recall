import { afterEach, expect, test, vi } from 'vitest'

import { readOutbox, removeFromOutbox, writeToOutbox, type OutboxEntry } from './outbox'
import { thisTab } from './tab-keys'

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

const wordsIn = (owner: string) => readOutbox(owner).map(({ entry }) => entry.change?.value)

test('the outbox of one person shows nothing to another person', () => {
  writeToOutbox('person-a', null, entryWith('words of a'))

  expect(readOutbox('person-b')).toEqual([])
  expect(wordsIn('person-a')).toEqual(['words of a'])
})

test('an owner whose name starts with the name of another owner has its own outbox', () => {
  writeToOutbox('ana.reyes', null, entryWith('words of ana reyes'))

  expect(readOutbox('ana')).toEqual([])
  expect(wordsIn('ana.reyes')).toEqual(['words of ana reyes'])
})

test('a key names the tab that wrote it, and a new text gets a new key', () => {
  const first = writeToOutbox('person-a', null, entryWith('first'))
  const second = writeToOutbox('person-a', first, entryWith('second'))

  expect(second).not.toBe(first)
  expect(readOutbox('person-a')).toEqual([expect.objectContaining({ storageKey: second, writer: thisTab })])
  expect(first && localStorage.getItem(first)).toBeNull()
})

test('a remove takes only the key that it names', () => {
  const first = writeToOutbox('person-a', null, entryWith('first'))
  writeToOutbox('person-a', null, entryWith('second'))

  removeFromOutbox(first ?? '')

  expect(wordsIn('person-a')).toEqual(['second'])
})

for (const [name, stored] of [
  ['text that is not JSON', '{not json'],
  ['null', 'null'],
  ['a number', '42'],
  ['an entry with no change', JSON.stringify({ ...entryWith('x'), change: undefined })],
  ['an unknown entry with no words', JSON.stringify({ ...entryWith('x'), channel: 'other', change: { value: '', savedAt: '2026-11-06T15:31:00.000Z' } })],
] as const) {
  test(`${name} does not hide the other words, and it goes from storage`, () => {
    writeToOutbox('person-a', null, entryWith('good words'))
    localStorage.setItem('recall.unsent.person-a.broken', stored)

    expect(wordsIn('person-a')).toEqual(['good words'])
    expect(localStorage.getItem('recall.unsent.person-a.broken')).toBeNull()
  })
}

test('an entry that this version cannot read keeps its words as a new entry, in the same key until a tab takes it', () => {
  const stored = JSON.stringify({ ...entryWith('words of an old version'), channel: 'other' })
  localStorage.setItem('recall.unsent.person-a.unknown', stored)

  const [found] = readOutbox('person-a')

  expect(found).toMatchObject({ writer: null, storageKey: 'recall.unsent.person-a.unknown', entry: { channel: 'raw', sessionId: null, change: { value: 'words of an old version' } } })
  expect(localStorage.getItem('recall.unsent.person-a.unknown')).toBe(stored)
})

test('a key with a name of an older version has no writer, so any tab can take it', () => {
  localStorage.setItem('recall.unsent.person-a.client-session.raw', JSON.stringify(entryWith('old words')))

  expect(readOutbox('person-a')[0]).toMatchObject({ writer: null, entry: { change: { value: 'old words' } } })
})

test('held words with no queue are an entry', () => {
  writeToOutbox('person-a', null, { ...entryWith('x'), change: null, held: 'held words' })

  expect(readOutbox('person-a')[0]?.entry).toMatchObject({ change: null, held: 'held words' })
})

test('a browser that refuses storage answers with no key and loses no other words', () => {
  const kept = writeToOutbox('person-a', null, entryWith('kept'))
  const setItem = vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
    throw new DOMException('full', 'QuotaExceededError')
  })

  expect(writeToOutbox('person-a', null, entryWith('new'))).toBeNull()

  setItem.mockRestore()
  expect(kept && localStorage.getItem(kept)).toContain('kept')
  expect(wordsIn('person-a')).toEqual(['kept'])
})
