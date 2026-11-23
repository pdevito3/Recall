import { expect, test } from 'vitest'

import type { PersonDto } from '@/api/generated/models/types/person-dto'

import { candidatesOf } from './proposal-candidates'

/** SES-56: the page offers the People that `PersonProposalApproval.Decide` on the Server reads. */
const person = (id: string, label: string): PersonDto => ({ id, label, version: 1 })
const idsOf = (label: string, people: PersonDto[], matchedPersonIds: string[] = []) =>
  candidatesOf({ label, matchedPersonIds, isNew: false, contexts: [] }, people).map(({ id }) => id)

test('the stored matches come first in their order, also after a rename, and a stored match that is gone drops out', () => {
  const people = [person('a', 'Jane'), person('b', 'Janet'), person('c', 'Jane')]

  expect(idsOf('Jane', people, ['gone', 'b', 'c'])).toEqual(['b', 'c', 'a'])
})

test.each([
  ['a hidden character', 'Ja\u200Bne', 'Jane'],
  ['a run of white space of any kind', 'Mary\u00A0\t jane', 'MARY JANE'],
  ['white space at the ends', ' jane\n', 'Jane'],
  ['a Greek letter with an iota below', '\u1F80', '\u1F88'],
])('%s matches', (_, label, other) => {
  expect(idsOf(label, [person('a', other)])).toEqual(['a'])
})

test.each([
  ['the sharp s and SS', 'Strau\u00DF', 'STRAUSS'],
  ['the dotless i and I', 'I\u0131', 'II'],
  ['a joiner that is part of a word', 'Jane\u200D', 'Jane'],
])('%s do not match, as on the Server', (_, label, other) => {
  expect(idsOf(label, [person('a', other)])).toEqual([])
})
