interface ProseNode {
  type?: unknown
  text?: unknown
  content?: unknown
  attrs?: { label?: unknown }
  marks?: unknown
}

function addressOf(node: ProseNode | undefined): string | undefined {
  if (!Array.isArray(node?.marks)) return undefined
  const link = (node.marks as { type?: unknown; attrs?: { href?: unknown } }[]).find((mark) => mark.type === 'link')
  return typeof link?.attrs?.href === 'string' ? link.attrs.href : undefined
}

/** Plain words have no link, so the address follows the words of its link, unless those words are the address. */
function withAddresses(nodes: ProseNode[]): string[] {
  return nodes.map((node, index) => {
    const text = textOf(node)
    const address = addressOf(node)
    const endsLink = address !== undefined && addressOf(nodes[index + 1]) !== address
    return endsLink && !address.endsWith(text) ? `${text} (${address})` : text
  })
}

function textOf(node: ProseNode): string {
  if (typeof node.text === 'string') return node.text
  if (node.type === 'mention' && typeof node.attrs?.label === 'string') return node.attrs.label
  if (node.type === 'hardBreak') return '\n'
  if (!Array.isArray(node.content)) return ''
  const separator = node.type === 'paragraph' || node.type === 'heading' || node.type === 'codeBlock' ? '' : '\n\n'
  return withAddresses(node.content as ProseNode[]).join(separator)
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
