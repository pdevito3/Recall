import type { JSONContent } from '@tiptap/react'

import { parseDocument } from './content'

/** A diff of two lists costs this many cells at most. Past it, the whole middle counts as one change, and no word goes. */
const mostDiffCells = 2_000_000

interface Hunk {
  baseStart: number
  baseEnd: number
  start: number
  end: number
}

/** The pairs of equal items of the longest common subsequence, as an index of `other` for each item of `base`, or -1. `same` tells which items are equal. */
function matchOf<T>(base: T[], other: T[], same: (one: T, other: T) => boolean = Object.is): number[] {
  const match = new Array<number>(base.length).fill(-1)
  let head = 0
  while (head < base.length && head < other.length && same(base[head], other[head])) {
    match[head] = head
    head += 1
  }
  let baseTail = base.length
  let otherTail = other.length
  while (baseTail > head && otherTail > head && same(base[baseTail - 1], other[otherTail - 1])) {
    baseTail -= 1
    otherTail -= 1
    match[baseTail] = otherTail
  }
  const rows = baseTail - head
  const columns = otherTail - head
  if (rows === 0 || columns === 0 || (rows + 1) * (columns + 1) > mostDiffCells) return match
  const lengths = new Uint32Array((rows + 1) * (columns + 1))
  const at = (row: number, column: number) => row * (columns + 1) + column
  for (let row = rows - 1; row >= 0; row -= 1) {
    for (let column = columns - 1; column >= 0; column -= 1) {
      lengths[at(row, column)] = same(base[head + row], other[head + column])
        ? lengths[at(row + 1, column + 1)] + 1
        : Math.max(lengths[at(row + 1, column)], lengths[at(row, column + 1)])
    }
  }
  let row = 0
  let column = 0
  while (row < rows && column < columns) {
    if (same(base[head + row], other[head + column])) {
      match[head + row] = head + column
      row += 1
      column += 1
    } else if (lengths[at(row + 1, column)] >= lengths[at(row, column + 1)]) row += 1
    else column += 1
  }
  return match
}

function hunksOf(base: string[], other: string[]): Hunk[] {
  const match = matchOf(base, other)
  const hunks: Hunk[] = []
  let baseAt = 0
  let otherAt = 0
  for (let index = 0; index <= base.length; index += 1) {
    const matched = index === base.length ? other.length : match[index]
    if (matched < 0) continue
    if (index > baseAt || matched > otherAt) hunks.push({ baseStart: baseAt, baseEnd: index, start: otherAt, end: matched })
    baseAt = index + 1
    otherAt = matched + 1
  }
  return hunks
}

/** Two changes touch when their ranges of the base cross, or when both add at one place. A change that ends where the other starts does not touch it. */
const touch = (one: Hunk, other: Hunk) =>
  (one.baseStart < other.baseEnd && other.baseStart < one.baseEnd)
  || (one.baseStart === other.baseStart && (one.baseStart === one.baseEnd || other.baseStart === other.baseEnd))

type Resolve<T> = (base: T[], mine: T[], theirs: T[]) => T[]

/** The items that `items` holds and `held` does not, each counted as many times as it comes. */
function missing<T>(items: T[], held: T[], key: (item: T) => string): T[] {
  const counts = new Map<string, number>()
  for (const item of held) counts.set(key(item), (counts.get(key(item)) ?? 0) + 1)
  return items.filter((item) => {
    const count = counts.get(key(item)) ?? 0
    if (count > 0) counts.set(key(item), count - 1)
    return count === 0
  })
}

/**
 * Where both sides changed one place in different ways, the run of the other side shows in full, then the run of the
 * person in full, so no item goes. Two people who type the same word both keep it. An item of the base that the other
 * side took out stays out.
 */
const theirsThenMine = <T>(key: (item: T) => string): Resolve<T> => (base, mine, theirs) => [...theirs, ...missing(mine, base, key)]

/**
 * A three-way merge: the change from `base` to `mine` goes into `theirs`. Each change of only one side applies.
 * Where both sides changed one place, `resolve` decides.
 */
function mergeLists<T>(base: T[], mine: T[], theirs: T[], key: (item: T) => string, resolve: Resolve<T>): T[] {
  const [baseKeys, mineKeys, theirKeys] = [base, mine, theirs].map((list) => list.map(key))
  const changes = [
    ...hunksOf(baseKeys, mineKeys).map((hunk) => ({ ...hunk, isMine: true })),
    ...hunksOf(baseKeys, theirKeys).map((hunk) => ({ ...hunk, isMine: false })),
  ].sort((one, other) => one.baseStart - other.baseStart || one.baseEnd - other.baseEnd)
  const merged: T[] = []
  let baseAt = 0
  let index = 0
  while (index < changes.length) {
    const group = [changes[index]]
    let baseEnd = changes[index].baseEnd
    index += 1
    while (index < changes.length && group.some((hunk) => touch(hunk, changes[index]))) {
      baseEnd = Math.max(baseEnd, changes[index].baseEnd)
      group.push(changes[index])
      index += 1
    }
    const baseStart = group[0].baseStart
    merged.push(...base.slice(baseAt, baseStart))
    const sideOf = (list: T[], isMine: boolean) => {
      const own = group.filter((hunk) => hunk.isMine === isMine)
      if (own.length === 0) return base.slice(baseStart, baseEnd)
      // The side holds the base items of the group that its own changes leave.
      const parts: T[] = []
      let at = baseStart
      for (const hunk of own) {
        parts.push(...base.slice(at, hunk.baseStart), ...list.slice(hunk.start, hunk.end))
        at = hunk.baseEnd
      }
      return [...parts, ...base.slice(at, baseEnd)]
    }
    const groupBase = base.slice(baseStart, baseEnd)
    const ofMine = sideOf(mine, true)
    const ofTheirs = sideOf(theirs, false)
    const same = (one: T[], other: T[]) => one.length === other.length && one.every((item, at) => key(item) === key(other[at]))
    if (!group.some((hunk) => hunk.isMine) || same(ofMine, groupBase) || same(ofMine, ofTheirs)) merged.push(...ofTheirs)
    else if (!group.some((hunk) => !hunk.isMine) || same(ofTheirs, groupBase)) merged.push(...ofMine)
    else merged.push(...resolve(groupBase, ofMine, ofTheirs))
    baseAt = baseEnd
  }
  merged.push(...base.slice(baseAt))
  return merged
}

const childrenOf = (node: JSONContent): JSONContent[] =>
  Array.isArray(node.content) ? node.content.filter((child) => typeof child === 'object' && child !== null) : []

const asDocument = (text: string): JSONContent =>
  parseDocument(text) ?? { type: 'doc', content: text ? [{ type: 'paragraph', content: [{ type: 'text', text }] }] : [] }

/** A word with the space before it. A mention or a line break is one word. */
interface Word {
  space: string
  node: JSONContent
}

const isParagraphOfWords = (block: JSONContent) =>
  block.type === 'paragraph' && Object.keys(block).every((name) => name === 'type' || name === 'content')

function wordsOf(paragraph: JSONContent): Word[] {
  const words: Word[] = []
  let space = ''
  for (const node of childrenOf(paragraph)) {
    if (node.type !== 'text') {
      words.push({ space, node })
      space = ''
      continue
    }
    for (const part of (node.text ?? '').split(/(\s+)/)) {
      if (part === '') continue
      if (/^\s+$/.test(part)) space += part
      else {
        words.push({ space, node: { ...node, text: part } })
        space = ''
      }
    }
  }
  return words
}

const keyOfWord = ({ node }: Word) => JSON.stringify(node)

const paragraphBreak: Word = { space: '', node: { type: 'paragraphBreak' } }

const wordsOfParagraphs = (paragraphs: JSONContent[]) =>
  paragraphs.flatMap((paragraph, at) => [...(at > 0 ? [paragraphBreak] : []), ...wordsOf(paragraph)])

/** The words in one block that holds words, such as a paragraph or a heading, inside the frame of that block. */
function textBlockOf(words: Word[], shell: JSONContent): JSONContent {
  const content: JSONContent[] = []
  words.forEach(({ space, node }, at) => {
    const gap = at === 0 ? '' : space || ' '
    const pieces: JSONContent[] = node.type === 'text' ? [{ ...node, text: gap + (node.text ?? '') }] : [...(gap ? [{ type: 'text', text: gap }] : []), node]
    for (const piece of pieces) {
      const last = content[content.length - 1] as JSONContent | undefined
      if (piece.type === 'text' && last?.type === 'text' && JSON.stringify(last.marks ?? []) === JSON.stringify(piece.marks ?? [])) {
        content[content.length - 1] = { ...last, text: (last.text ?? '') + (piece.text ?? '') }
      } else content.push(piece)
    }
  })
  return content.length > 0 ? { ...shell, content } : shell
}

const paragraphOf = (words: Word[]) => textBlockOf(words, { type: 'paragraph' })

function paragraphsOf(words: Word[]): JSONContent[] {
  const paragraphs: Word[][] = [[]]
  for (const word of words) {
    if (word.node.type === paragraphBreak.node.type) paragraphs.push([])
    else paragraphs[paragraphs.length - 1].push(word)
  }
  return paragraphs.map(paragraphOf)
}

/** The items that both lists hold, in order. With no known base, this is the base: each item that only one side holds is an addition of that side. */
function commonOf<T>(one: T[], other: T[], key: (item: T) => string): T[] {
  const match = matchOf(one.map(key), other.map(key))
  return one.filter((_, at) => match[at] >= 0)
}

const keyOfBlock = (block: JSONContent) => JSON.stringify(block)

/** A block with no content: its type, its attributes, and its marks. */
const shellOf = (block: JSONContent): JSONContent => Object.fromEntries(Object.entries(block).filter(([name]) => name !== 'content'))

/** A block that holds words, and no block. */
const holdsWords = (block: JSONContent) => childrenOf(block).every((child) => child.type === 'text' || !Array.isArray(child.content))

interface Outline {
  key: string
  shell: string
  words: Set<string>
}

const textsOf = (node: JSONContent): string[] => (node.type === 'text' ? (node.text ?? '').split(/\s+/).filter(Boolean) : childrenOf(node).flatMap(textsOf))

const outlinesOf = (blocks: JSONContent[]): Outline[] => blocks.map((block) => ({ key: keyOfBlock(block), shell: keyOfBlock(shellOf(block)), words: new Set(textsOf(block)) }))

function sharesAWord(one: Set<string>, other: Set<string>) {
  for (const word of one) if (other.has(word)) return true
  return false
}

/** Two blocks are one block on two sides when they have one type and attributes, and they are equal or share a word. */
const isOneBlock = (one: Outline, other: Outline) =>
  one.shell === other.shell && (one.key === other.key || sharesAWord(one.words, other.words))

const matchBlocksOf = (one: JSONContent[], other: JSONContent[]) => matchOf(outlinesOf(one), outlinesOf(other), isOneBlock)

/** With no known base, the base of two lists of blocks: each block that both sides hold, with the words and blocks that both hold. */
function commonBlocksOf(one: JSONContent[], other: JSONContent[]): JSONContent[] {
  const match = matchBlocksOf(one, other)
  return one.flatMap((block, at) => (match[at] < 0 ? [] : [commonBlockOf(block, other[match[at]])]))
}

function commonBlockOf(one: JSONContent, other: JSONContent): JSONContent {
  if (keyOfBlock(one) === keyOfBlock(other)) return one
  if (holdsWords(one) && holdsWords(other)) return textBlockOf(commonOf(wordsOf(one), wordsOf(other), keyOfWord), shellOf(one))
  return { ...shellOf(one), content: commonBlocksOf(childrenOf(one), childrenOf(other)) }
}

const mergeBlocks = (base: JSONContent[], mine: JSONContent[], theirs: JSONContent[], hasBase: boolean): JSONContent[] =>
  mergeLists(base, mine, theirs, keyOfBlock, resolveBlocks(hasBase))

/** The places of each block of `base` that both sides kept, in order: in `base`, in `mine`, and in `theirs`. */
function keptBlocksOf(base: JSONContent[], mine: JSONContent[], theirs: JSONContent[]): [number, number, number][] {
  const [inMine, inTheirs] = [matchBlocksOf(base, mine), matchBlocksOf(base, theirs)]
  return base.flatMap((_, at) => (inMine[at] >= 0 && inTheirs[at] >= 0 ? [[at, inMine[at], inTheirs[at]] as [number, number, number]] : []))
}

/**
 * Each block that both sides kept merges on its own, so two changed blocks side by side do not show twice. The blocks
 * between them merge as blocks. Null when no block of `base` stays on both sides.
 */
function mergeBlockByBlock(base: JSONContent[], mine: JSONContent[], theirs: JSONContent[], hasBase: boolean): JSONContent[] | null {
  const kept = keptBlocksOf(base, mine, theirs)
  if (kept.length === 0) return null
  const merge = (ofBase: JSONContent[], ofMine: JSONContent[], ofTheirs: JSONContent[]) => mergeBlocks(ofBase, ofMine, ofTheirs, hasBase)
  const merged: JSONContent[] = []
  let [baseAt, mineAt, theirAt] = [0, 0, 0]
  for (const [atBase, atMine, atTheirs] of [...kept, [base.length, mine.length, theirs.length]]) {
    merged.push(...merge(base.slice(baseAt, atBase), mine.slice(mineAt, atMine), theirs.slice(theirAt, atTheirs)))
    if (atBase < base.length) merged.push(...merge([base[atBase]], [mine[atMine]], [theirs[atTheirs]]))
    ;[baseAt, mineAt, theirAt] = [atBase + 1, atMine + 1, atTheirs + 1]
  }
  return merged
}

/**
 * Where both sides changed the same paragraphs, they merge word by word, so a word that both hold stays one word.
 * Where both sides changed one other block, such as a list or a heading, and kept its type and attributes, its content
 * merges the same way, item by item and word by word, so the block shows once.
 * Where both sides changed blocks side by side, such as a heading and a paragraph, each block merges on its own.
 * New paragraphs that both sides added at one place stay paragraphs: theirs, and then mine. Other blocks do the same.
 * With no known base, paragraphs that differ merge word by word, as in the merge of the iOS client.
 */
const resolveBlocks = (hasBase: boolean): Resolve<JSONContent> => (base, mine, theirs) => {
  const isAddedByBoth = hasBase && base.length === 0
  if (!isAddedByBoth && [...base, ...mine, ...theirs].every(isParagraphOfWords)) {
    const [ofMine, ofTheirs] = [wordsOfParagraphs(mine), wordsOfParagraphs(theirs)]
    const ofBase = base.length > 0 ? wordsOfParagraphs(base) : commonOf(ofMine, ofTheirs, keyOfWord)
    return paragraphsOf(mergeLists(ofBase, ofMine, ofTheirs, keyOfWord, theirsThenMine(keyOfWord)))
  }
  const shell = mine.length === 1 && theirs.length === 1 ? shellOf(theirs[0]) : null
  const isOneBlockChanged = shell !== null && !isAddedByBoth && base.length <= 1
    && [...base, ...mine].every((block) => keyOfBlock(shellOf(block)) === keyOfBlock(shell))
  if (!isOneBlockChanged) {
    const byBlock = isAddedByBoth ? null : mergeBlockByBlock(base, mine, theirs, hasBase)
    return byBlock ?? theirsThenMine(keyOfBlock)(base, mine, theirs)
  }
  const blocks = [...base, ...mine, ...theirs]
  if (blocks.every(holdsWords)) {
    const [ofMine, ofTheirs] = [wordsOf(mine[0]), wordsOf(theirs[0])]
    const ofBase = base.length > 0 ? wordsOf(base[0]) : commonOf(ofMine, ofTheirs, keyOfWord)
    return [textBlockOf(mergeLists(ofBase, ofMine, ofTheirs, keyOfWord, theirsThenMine(keyOfWord)), shell)]
  }
  const [ofMine, ofTheirs] = [childrenOf(mine[0]), childrenOf(theirs[0])]
  const ofBase = base.length > 0 ? childrenOf(base[0]) : commonBlocksOf(ofMine, ofTheirs)
  return [{ ...shell, content: mergeBlocks(ofBase, ofMine, ofTheirs, hasBase) }]
}

/**
 * With no known base, a block of the person that matches a block of the other side at another place is one block that
 * a merge before put at two places. Its words that the other side holds show there, and only the other words stay at
 * the place of the person. This is the case of a key that two tabs hold: each tab merges the same words.
 */
function withoutWordsAtTheirPlace(mine: JSONContent[], theirs: JSONContent[]): JSONContent[] {
  const match = matchBlocksOf(mine, theirs)
  const taken = new Set(match.filter((at) => at >= 0))
  const outlinesOfTheirs = outlinesOf(theirs)
  return mine.flatMap((block, at) => {
    if (match[at] >= 0 || !holdsWords(block)) return [block]
    const [outline] = outlinesOf([block])
    const other = outlinesOfTheirs.findIndex((ofTheirs, theirAt) => !taken.has(theirAt) && holdsWords(theirs[theirAt]) && isOneBlock(outline, ofTheirs))
    if (other < 0) return [block]
    taken.add(other)
    const left = missing(wordsOf(block), wordsOf(theirs[other]), keyOfWord)
    return left.length > 0 ? [textBlockOf(left, shellOf(block))] : []
  })
}

/**
 * The change of the person since `base` goes into the newest Draft, `theirs`, one time, as the steps after `Lost` in
 * `docs/ios-api-changes.md` ask. A block that only the person changed takes the change, so its words do not show twice.
 * Paragraphs that both sides changed merge word by word. A change of only the other side stays.
 * An empty Draft is a base like any other, so two tabs that each typed into it keep all of their words.
 * With no known base, the words that both hold are the base, as in the merge of the iOS client: a word that only one side holds stays.
 */
export function mergeDraft(base: string | null, mine: string, theirs: string): string {
  const blocks = (text: string) => childrenOf(asDocument(text))
  const [ofMine, ofTheirs] = [blocks(mine), blocks(theirs)]
  if (base !== null) return JSON.stringify({ type: 'doc', content: mergeBlocks(blocks(base), ofMine, ofTheirs, true) })
  const ofMineHere = withoutWordsAtTheirPlace(ofMine, ofTheirs)
  return JSON.stringify({ type: 'doc', content: mergeBlocks(commonBlocksOf(ofMineHere, ofTheirs), ofMineHere, ofTheirs, false) })
}
