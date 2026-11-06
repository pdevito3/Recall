import { expect, test } from 'vitest'

import { withTopicAdded } from './tags'

test('a topic is trimmed before it is added', () => {
  expect(withTopicAdded(['work'], '  home ')).toEqual(['work', 'home'])
})

test('a blank topic and a topic that differs only in case add nothing', () => {
  expect(withTopicAdded(['work'], '   ')).toEqual(['work'])
  expect(withTopicAdded(['work'], 'WORK')).toEqual(['work'])
})
