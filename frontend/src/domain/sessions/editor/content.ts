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

const blockTypes = new Set(['paragraph', 'heading', 'blockquote', 'listItem', 'taskItem', 'codeBlock', 'hardBreak'])

function plainTextOf(node: JSONContent): string {
  if (node.type === 'text') return node.text ?? ''
  if (node.type === 'mention') return String(node.attrs?.label ?? '')
  const inner = (node.content ?? []).map(plainTextOf).join('')
  return blockTypes.has(node.type ?? '') ? `${inner} ` : inner
}

export function plainPreviewOf(text: string, length: number): string {
  const document = parseDocument(text)
  const plain = document ? plainTextOf(document) : text
  return plain.replace(/\s+/g, ' ').trim().slice(0, length)
}
