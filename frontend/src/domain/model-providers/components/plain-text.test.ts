import { describe, expect, it } from 'vitest'

import { plainTextOf } from './plain-text'

describe('plainTextOf', () => {
  it('shows the words of each paragraph and the label of a mention', () => {
    const doc = JSON.stringify({
      type: 'doc',
      content: [
        { type: 'paragraph', content: [{ type: 'text', text: 'I met ' }, { type: 'mention', attrs: { id: 'per_1', label: 'Sam' } }] },
        { type: 'paragraph', content: [{ type: 'text', text: 'It went well.' }] },
      ],
    })

    expect(plainTextOf(doc)).toBe('I met Sam\n\nIt went well.')
  })

  it.each(['plain words', '{"cut', '{"type":"other"}', '12', ''])('keeps text that is no document: %s', (stored) => {
    expect(plainTextOf(stored)).toBe(stored)
  })
})
