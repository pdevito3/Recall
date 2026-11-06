import { expect, test } from 'vitest'

import { contentOf, plainPreviewOf } from './content'

const doc = (...content: object[]) => JSON.stringify({ type: 'doc', content })
const paragraph = (text: string) => ({ type: 'paragraph', content: [{ type: 'text', text }] })

test('a document opens as itself', () => {
  expect(contentOf(doc(paragraph('Hello')))).toEqual({ type: 'doc', content: [paragraph('Hello')] })
})

test('plain words from another client open as paragraphs, and no word is lost', () => {
  expect(contentOf('one\n\ntwo')).toEqual({ type: 'doc', content: [paragraph('one'), { type: 'paragraph' }, paragraph('two')] })
})

test.each(['{not json', '{"type":"other"}', '[1,2]', 'null'])('the text %s opens as words', (text) => {
  expect(contentOf(text)).toEqual({ type: 'doc', content: [paragraph(text)] })
})

test('the preview reads the words and the mentions of each block', () => {
  const text = doc(
    { type: 'paragraph', content: [{ type: 'text', text: 'Met ' }, { type: 'mention', attrs: { personId: '1', label: 'Ana' } }] },
    paragraph('then lunch'),
  )

  expect(plainPreviewOf(text, 200)).toBe('Met Ana then lunch')
})

test('the preview of plain words is those words, cut to the length', () => {
  expect(plainPreviewOf('one two three', 7)).toBe('one two')
})

test('a document with a node that the editor does not know shows its text, so the words are not blank', () => {
  const text = JSON.stringify({ type: 'doc', content: [{ type: 'hologram', content: [{ type: 'text', text: 'Words' }] }] })

  expect(contentOf(text)).toEqual({ type: 'doc', content: [{ type: 'paragraph', content: [{ type: 'text', text }] }] })
})
