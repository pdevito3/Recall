import { expect, test } from 'vitest'

import { hangingIndentOf } from './hanging-indent'

test('@IMP-46 a wrapped line of a list item starts under the words of the item', () => {
  expect(hangingIndentOf('- `list_journal_days` gives the days')).toBe(2)
  expect(hangingIndentOf('10. Write the full batch.')).toBe(4)
  expect(hangingIndentOf('    Slept late. Pancakes with Ana.')).toBe(4)
  expect(hangingIndentOf('  * an item in an item')).toBe(4)
})

test('@IMP-46 a line of prose, a heading, and an empty line have no hanging indent', () => {
  expect(hangingIndentOf('You read the source.')).toBe(0)
  expect(hangingIndentOf('## The tools')).toBe(0)
  expect(hangingIndentOf('---')).toBe(0)
  expect(hangingIndentOf('')).toBe(0)
  expect(hangingIndentOf('-5 degrees today')).toBe(0)
})

test('@IMP-46 the line break at the end of a line is not part of the indent', () => {
  expect(hangingIndentOf('\n')).toBe(0)
  expect(hangingIndentOf('- item\n')).toBe(2)
})
