import type { JSONContent } from '@tiptap/react'

function isDocument(value: unknown): value is JSONContent {
  return typeof value === 'object' && value !== null && (value as JSONContent).type === 'doc'
}

export function parseDocument(text: string): JSONContent | null {
  if (!text.trimStart().startsWith('{')) return null
  try {
    const parsed: unknown = JSON.parse(text)
    return isDocument(parsed) ? parsed : null
  } catch {
    return null
  }
}

// Another client can save any text, so a document can hold nodes that the editor would not write.
const childrenOf = (node: JSONContent): JSONContent[] =>
  Array.isArray(node.content) ? node.content.filter((child) => typeof child === 'object' && child !== null) : []

const blockTypes = new Set(['paragraph', 'heading', 'blockquote', 'listItem', 'taskItem', 'codeBlock', 'hardBreak'])

function plainTextOf(node: JSONContent): string {
  if (node.type === 'text') return node.text ?? ''
  if (node.type === 'mention') return String(node.attrs?.label ?? '')
  const inner = childrenOf(node).map(plainTextOf).join('')
  return blockTypes.has(node.type ?? '') ? `${inner} ` : inner
}

export function plainPreviewOf(text: string, length: number): string {
  const document = parseDocument(text)
  const plain = document ? plainTextOf(document) : text
  return plain.replace(/\s+/g, ' ').trim().slice(0, length)
}

export function plainWordsOf(text: string): string {
  const document = parseDocument(text)
  if (!document) return text
  const lineOf = (node: JSONContent): string => {
    const children = childrenOf(node)
    const holdsWords = blockTypes.has(node.type ?? '') || children.some((child) => child.type === 'text' || child.type === 'mention')
    return holdsWords ? plainTextOf(node).trimEnd() : children.map(lineOf).join('\n')
  }
  return lineOf(document)
}

const asDocument = (text: string): JSONContent =>
  parseDocument(text) ?? { type: 'doc', content: text ? [{ type: 'paragraph', content: [{ type: 'text', text }] }] : [] }

export function joinWords(first: string, second: string): string {
  const [one, two] = [asDocument(first), asDocument(second)]
  return JSON.stringify({ type: 'doc', content: [...childrenOf(one), ...childrenOf(two)] })
}

function personIdsIn(node: JSONContent): string[] {
  const personId: unknown = node.type === 'mention' ? node.attrs?.personId : null
  const own = typeof personId === 'string' && personId ? [personId] : []
  return [...own, ...childrenOf(node).flatMap(personIdsIn)]
}

export function mentionedPersonIds(raw: string | null, cleaned: string | null): string[] {
  const documents = [raw, cleaned].flatMap((text) => (text && parseDocument(text)) || [])
  return [...new Set(documents.flatMap(personIdsIn))].sort()
}
