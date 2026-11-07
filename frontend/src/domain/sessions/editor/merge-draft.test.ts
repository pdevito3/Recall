import { expect, test } from 'vitest'

import { plainWordsOf } from './content'
import { mergeDraft } from './merge-draft'

const doc = (...content: object[]) => JSON.stringify({ type: 'doc', content })
const paragraph = (text: string) => ({ type: 'paragraph', content: [{ type: 'text', text }] })
const words = (...paragraphs: string[]) => doc(...paragraphs.map(paragraph))
const plain = (text: string) => plainWordsOf(text).split('\n')

test('a paragraph that only the person changed takes the change, and the paragraphs of another device stay once', () => {
  const merged = mergeDraft(words('base'), words('base word1'), words('base', 'OTHERDEVICE', 'CHUNK'))

  expect(plain(merged)).toEqual(['base word1', 'OTHERDEVICE', 'CHUNK'])
})

test('a merge of what a merge wrote adds nothing', () => {
  const theirs = words('base', 'OTHERDEVICE', 'CHUNK')
  const merged = mergeDraft(words('base'), words('base word1'), theirs)

  expect(mergeDraft(theirs, merged, merged)).toBe(merged)
  expect(mergeDraft(null, merged, merged)).toBe(merged)
})

test('a paragraph that both sides changed merges word by word, and no word shows twice', () => {
  const merged = mergeDraft(words('I walked home'), words('I walked home slowly'), words('Today I walked home'))

  expect(plain(merged)).toEqual(['Today I walked home slowly'])
})

test('words that both sides added at one place keep the other side first, and each word of the person follows once', () => {
  const merged = mergeDraft(words('one'), words('one two'), words('one three'))

  expect(plain(merged)).toEqual(['one three two'])
})

test('words that both sides added at one place all stay, also the words that both typed', () => {
  const merged = mergeDraft(words('Words at the start.'), words('Words at the start. The second tab.'), words('Words at the start. The first tab.'))

  expect(plain(merged)).toEqual(['Words at the start. The first tab. The second tab.'])
})

test('a merge of words that a merge wrote into one paragraph adds nothing', () => {
  const [base, mine] = [words('Words at the start.'), words('Words at the start. The second tab.')]
  const merged = mergeDraft(base, mine, words('Words at the start. The first tab.'))

  expect(mergeDraft(base, mine, merged)).toBe(merged)
})

test('words of the person that the newest words hold in order, as after a save that won with no answer, do not show twice', () => {
  const merged = mergeDraft(words('base'), words('base the first save and more'), words('base the first save'))

  expect(plain(merged)).toEqual(['base the first save and more'])
})

test('new paragraphs that both sides added at one place stay paragraphs, theirs first', () => {
  const merged = mergeDraft(words('base'), words('base', 'my words'), words('base', 'a chunk'))

  expect(plain(merged)).toEqual(['base', 'a chunk', 'my words'])
})

test('with no known base, a paragraph that the person extends during a read keeps each word once', () => {
  const merged = mergeDraft(null, words('my words and a key'), words('my words', 'a chunk'))

  expect(plainWordsOf(merged).split(/\s+/).sort()).toEqual(['a', 'and', 'chunk', 'key', 'my', 'words'])
})

test('a paragraph that the person deleted goes, when the other device did not change it', () => {
  const merged = mergeDraft(words('keep', 'drop'), words('keep'), words('keep', 'drop', 'new'))

  expect(plain(merged)).toEqual(['keep', 'new'])
})

test('a change of only the other device stays', () => {
  const base = words('a', 'b')

  expect(mergeDraft(base, base, words('a', 'b', 'c'))).toBe(words('a', 'b', 'c'))
})

test('with no known base, each word of the person that the newest words lack follows them, and no word shows twice', () => {
  const merged = mergeDraft(null, words('base word1'), words('base OTHERDEVICE'))

  expect(plain(merged)).toEqual(['base OTHERDEVICE word1'])
})

test('a mention and a mark stay in a paragraph that merges word by word', () => {
  const mention = { type: 'mention', attrs: { personId: 'per_1', label: 'Sam' } }
  const bold = { type: 'text', text: 'loud', marks: [{ type: 'bold' }] }
  const base = doc({ type: 'paragraph', content: [{ type: 'text', text: 'with ' }, mention] })
  const mine = doc({ type: 'paragraph', content: [{ type: 'text', text: 'with ' }, mention, { type: 'text', text: ' and ' }, bold] })
  const theirs = doc({ type: 'paragraph', content: [{ type: 'text', text: 'Lunch with ' }, mention] })

  const merged = JSON.parse(mergeDraft(base, mine, theirs)) as { content: { content: object[] }[] }

  expect(merged.content[0].content).toEqual([{ type: 'text', text: 'Lunch with ' }, mention, { type: 'text', text: ' and' }, { type: 'text', text: ' loud', marks: [{ type: 'bold' }] }])
})

test('items that both sides added at the end of one list stay in that list: theirs, and then mine', () => {
  const merged = mergeDraft(doc(list('a')), doc(list('a', 'mine')), doc(list('a', 'theirs')))

  expect(JSON.parse(merged)).toEqual({ type: 'doc', content: [list('a', 'theirs', 'mine')] })
})

test('a long text with one change costs little and keeps each word', () => {
  const many = Array.from({ length: 20_000 }, (_, at) => `w${at}`).join(' ')
  const merged = mergeDraft(words(many), words(`${many} mine`), words(`theirs ${many}`))

  expect(plain(merged)).toEqual([`theirs ${many} mine`])
})

const list = (...items: string[]) => ({ type: 'bulletList', content: items.map((text) => ({ type: 'listItem', content: [paragraph(text)] })) })
const heading = (text: string, level = 2) => ({ type: 'heading', attrs: { level }, content: [{ type: 'text', text }] })

test('a list that both sides changed merges item by item, so the list shows once with each change', () => {
  const merged = mergeDraft(doc(list('milk', 'eggs')), doc(list('milk', 'eggs', 'bread')), doc(list('oat milk', 'eggs')))

  expect(JSON.parse(merged)).toEqual(JSON.parse(doc(list('oat milk', 'eggs', 'bread'))))
})

test('one item of a list that both sides changed merges word by word', () => {
  const merged = mergeDraft(doc(list('milk', 'eggs')), doc(list('milk and honey', 'eggs')), doc(list('cold milk', 'eggs')))

  expect(JSON.parse(merged)).toEqual(JSON.parse(doc(list('cold milk and honey', 'eggs'))))
})

test('a heading that both sides changed merges word by word and keeps its level', () => {
  const merged = mergeDraft(doc(heading('Day one')), doc(heading('Day one at sea')), doc(heading('Day 1')))

  expect(JSON.parse(merged)).toEqual(JSON.parse(doc(heading('Day 1 at sea'))))
})

test('with no known base, a list that each side extends shows once, with the items of both', () => {
  const merged = mergeDraft(null, doc(list('milk', 'eggs', 'bread')), doc(list('milk', 'eggs', 'jam')))

  expect(JSON.parse(merged).content).toHaveLength(1)
  expect(plainWordsOf(merged).split(/\s+/).sort()).toEqual(['bread', 'eggs', 'jam', 'milk'])
})

test('a heading whose level changed on one side and whose words changed on the other keeps both blocks, with no word lost', () => {
  const merged = mergeDraft(doc(heading('Day one')), doc(heading('Day one at sea')), doc(heading('Day one', 3)))

  expect(plainWordsOf(merged)).toContain('at sea')
})
