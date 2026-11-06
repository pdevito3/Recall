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

  it('keeps task items, the paragraphs of a quote, and a hard break apart', () => {
    const paragraph = (text: string) => ({ type: 'paragraph', content: [{ type: 'text', text }] })
    const doc = JSON.stringify({
      type: 'doc',
      content: [
        { type: 'taskList', content: [{ type: 'taskItem', content: [paragraph('buy milk')] }, { type: 'taskItem', content: [paragraph('call Sam')] }] },
        { type: 'blockquote', content: [paragraph('first'), paragraph('second')] },
        { type: 'paragraph', content: [{ type: 'text', text: 'one' }, { type: 'hardBreak' }, { type: 'text', text: 'two' }] },
      ],
    })

    expect(plainTextOf(doc)).toBe('buy milk\n\ncall Sam\n\nfirst\n\nsecond\n\none\ntwo')
  })

  it.each(['plain words', '{"cut', '{"type":"other"}', '12', ''])('keeps text that is no document: %s', (stored) => {
    expect(plainTextOf(stored)).toBe(stored)
  })
})
