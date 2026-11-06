import { expect, test } from 'vitest'

import { joinWords, mentionedPersonIds, plainPreviewOf, plainWordsOf } from './content'
import { contentOf } from './content-of'

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
  const text = JSON.stringify({ type: 'doc', content: [paragraph('First'), { type: 'hologram', content: [{ type: 'text', text: 'Words' }] }] })

  expect(contentOf(text)).toEqual({ type: 'doc', content: [paragraph('First'), paragraph('Words')] })
})

test('an empty document opens with no words', () => {
  const text = JSON.stringify({ type: 'doc', content: [] })

  expect(JSON.stringify(contentOf(text))).not.toContain('content\\"')
})

test('a document with words only in fields that the app does not read shows its text, so the page is not blank', () => {
  const text = JSON.stringify({ type: 'doc', blocks: [{ words: 'kept words' }] })

  expect(JSON.stringify(contentOf(text))).toContain('kept words')
})

test('the people of an entry are the mentions of each text, once each, in the order of the Server', () => {
  const mention = (personId: string) => ({ type: 'mention', attrs: { personId, label: personId } })
  const raw = doc({ type: 'paragraph', content: [mention('b'), mention('a')] })
  const cleaned = doc({ type: 'blockquote', content: [{ type: 'paragraph', content: [mention('a'), mention('c')] }] })

  expect(mentionedPersonIds(raw, cleaned)).toEqual(['a', 'b', 'c'])
})

test.each([['plain words'], ['{not json'], [null]])('the text %s mentions no one', (text) => {
  expect(mentionedPersonIds(text, null)).toEqual([])
})

test.each(['{"type":"doc","content":"x"}', '{"type":"doc","content":[null]}'])('the document %s reads as no words and no people', (text) => {
  expect(plainPreviewOf(text, 200)).toBe('')
  expect(mentionedPersonIds(text, null)).toEqual([])
})

test('words for a copy have a line for each block, and words that are not a document stay as they are', () => {
  expect(plainWordsOf(doc(paragraph('First'), paragraph('Second')))).toBe('First\nSecond')
  expect(plainWordsOf('Plain words')).toBe('Plain words')
})

test('joined words keep the first words and then the second', () => {
  expect(joinWords(doc(paragraph('Before')), doc(paragraph('After')))).toBe(doc(paragraph('Before'), paragraph('After')))
  expect(joinWords('Plain', doc(paragraph('After')))).toBe(doc(paragraph('Plain'), paragraph('After')))
  expect(joinWords('', doc(paragraph('After')))).toBe(doc(paragraph('After')))
})
