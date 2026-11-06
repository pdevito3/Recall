interface ProseNode {
  type?: unknown
  text?: unknown
  content?: unknown
  attrs?: { label?: unknown }
}

function textOf(node: ProseNode): string {
  if (typeof node.text === 'string') return node.text
  if (node.type === 'mention' && typeof node.attrs?.label === 'string') return node.attrs.label
  if (!Array.isArray(node.content)) return ''
  const separator = node.type === 'doc' || node.type === 'bulletList' || node.type === 'orderedList' ? '\n\n' : ''
  return (node.content as ProseNode[]).map(textOf).join(separator)
}

/** A Revision holds a ProseMirror document or plain words, and each form shows as words. */
export function plainTextOf(stored: string): string {
  try {
    const parsed: unknown = JSON.parse(stored)
    if (typeof parsed === 'object' && parsed !== null && (parsed as ProseNode).type === 'doc') {
      return textOf(parsed as ProseNode)
    }
  } catch {
    return stored
  }
  return stored
}
