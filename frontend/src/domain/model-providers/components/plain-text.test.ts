import { describe, expect, it } from 'vitest'

import { plainTextOf } from './plain-text'

describe('plainTextOf', () => {
  it('shows the address of a link once, after the words of the link', () => {
    const link = { type: 'link', attrs: { href: 'https://sam.example/2003' } }
    const doc = JSON.stringify({
      type: 'doc',
      content: [
        {
          type: 'paragraph',
          content: [
            { type: 'text', text: 'see ' },
            { type: 'text', text: 'the ', marks: [link] },
            { type: 'text', text: 'site', marks: [{ type: 'bold' }, link] },
            { type: 'text', text: ' now and ' },
            { type: 'text', text: 'sam@example.com', marks: [{ type: 'link', attrs: { href: 'mailto:sam@example.com' } }] },
          ],
        },
      ],
    })

    expect(plainTextOf(doc)).toBe('see the site (https://sam.example/2003) now and sam@example.com')
  })

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

  it('keeps the state of a task, the paragraphs of a quote, and a hard break', () => {
    const paragraph = (text: string) => ({ type: 'paragraph', content: [{ type: 'text', text }] })
    const doc = JSON.stringify({
      type: 'doc',
      content: [
        {
          type: 'taskList',
          content: [
            { type: 'taskItem', attrs: { checked: false }, content: [paragraph('buy milk')] },
            { type: 'taskItem', attrs: { checked: true }, content: [paragraph('call Sam')] },
          ],
        },
        { type: 'blockquote', content: [paragraph('first'), paragraph('second')] },
        { type: 'paragraph', content: [{ type: 'text', text: 'one' }, { type: 'hardBreak' }, { type: 'text', text: 'two' }] },
      ],
    })

    expect(plainTextOf(doc)).toBe('[ ] buy milk\n[x] call Sam\n\n> first\n>\n> second\n\none\ntwo')
  })

  it('keeps the sign of an item, because "- 5 degrees" is not "5 degrees"', () => {
    const item = (text: string) => ({ type: 'listItem', content: [{ type: 'paragraph', content: [{ type: 'text', text }] }] })
    const doc = JSON.stringify({ type: 'doc', content: [{ type: 'bulletList', content: [item('5 degrees'), item('6 degrees')] }] })

    expect(plainTextOf(doc)).toBe('- 5 degrees\n- 6 degrees')
  })

  it('counts the numbers of a list from its start, and indents a nested list', () => {
    const paragraph = (text: string) => ({ type: 'paragraph', content: [{ type: 'text', text }] })
    const nested = { type: 'bulletList', content: [{ type: 'listItem', content: [paragraph('under')] }] }
    const doc = JSON.stringify({
      type: 'doc',
      content: [{ type: 'orderedList', attrs: { start: 1 }, content: [{ type: 'listItem', content: [paragraph('one'), nested] }, { type: 'listItem', content: [paragraph('two')] }] }],
    })

    expect(plainTextOf(doc)).toBe('1. one\n   - under\n2. two')
  })

  it('keeps a strike, because "I do ~~not~~ love him" is not "I do not love him"', () => {
    const strike = { type: 'strike' }
    const doc = JSON.stringify({
      type: 'doc',
      content: [
        {
          type: 'paragraph',
          content: [
            { type: 'text', text: 'I do ' },
            { type: 'text', text: 'not', marks: [strike] },
            { type: 'text', text: ' at', marks: [strike, { type: 'bold' }] },
            { type: 'text', text: ' love him' },
          ],
        },
      ],
    })

    expect(plainTextOf(doc)).toBe('I do ~~not at~~ love him')
  })

  it('shows an address when the words of its link only end the same way', () => {
    const link = (href: string) => [{ type: 'link', attrs: { href } }]
    const doc = JSON.stringify({
      type: 'doc',
      content: [
        {
          type: 'paragraph',
          content: [
            { type: 'text', text: 'site', marks: link('https://evil.example/site') },
            { type: 'text', text: ' and ' },
            { type: 'text', text: 'https://sam.example', marks: link('https://sam.example') },
          ],
        },
      ],
    })

    expect(plainTextOf(doc)).toBe('site (https://evil.example/site) and https://sam.example')
  })

  it('shows a code block with its language in a fence and a rule as a rule', () => {
    const doc = JSON.stringify({
      type: 'doc',
      content: [
        { type: 'codeBlock', attrs: { language: 'js' }, content: [{ type: 'text', text: 'a\nb' }] },
        { type: 'horizontalRule' },
      ],
    })

    expect(plainTextOf(doc)).toBe('```js\na\nb\n```\n\n---')
  })

  it.each(['plain words', '{"cut', '{"type":"other"}', '12', ''])('keeps text that is no document: %s', (stored) => {
    expect(plainTextOf(stored)).toBe(stored)
  })
})
