import { describe, expect, test } from 'vitest'

import { shownPreviewOf } from './preview'

describe('the preview of a row', () => {
  test('a short preview stays as it is', () => {
    expect(shownPreviewOf('Morning coffee')).toBe('Morning coffee')
  })

  test('a preview at the limit of the Server ends at a word and shows the cut', () => {
    const preview = `${'word '.repeat(39)}inter`
    expect([...preview]).toHaveLength(200)
    expect(shownPreviewOf(preview)).toBe(`${'word '.repeat(38)}word\u2026`)
  })

  test('words with no space keep all of their characters and show the cut', () => {
    expect(shownPreviewOf('x'.repeat(200))).toBe(`${'x'.repeat(200)}\u2026`)
  })

  test('a long last word does not take most of the preview away', () => {
    const preview = `a ${'x'.repeat(198)}`
    expect(shownPreviewOf(preview)).toBe(`${preview}\u2026`)
  })

  test('a character outside the first plane counts once, as on the Server', () => {
    const preview = '\u{1F642}'.repeat(199)
    expect(shownPreviewOf(preview)).toBe(preview)
  })
})
