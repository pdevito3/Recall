import { expect, test } from 'vitest'

import { optionsFor } from './mention-suggestion'

const people = [
  { id: '1', label: 'Ana Reyes' },
  { id: '2', label: 'Ben Stone' },
]

test('an empty query lists each person and offers no new person', () => {
  expect(optionsFor(people, '').map((option) => option.label)).toEqual(['Ana Reyes', 'Ben Stone'])
})

test('a query matches any part of the name, with case ignored, and offers a new person', () => {
  expect(optionsFor(people, 'sto')).toEqual([
    { kind: 'person', personId: '2', label: 'Ben Stone' },
    { kind: 'create', label: 'sto' },
  ])
})

test('an exact name offers no new person of that name', () => {
  expect(optionsFor(people, 'ana reyes')).toEqual([{ kind: 'person', personId: '1', label: 'Ana Reyes' }])
})
