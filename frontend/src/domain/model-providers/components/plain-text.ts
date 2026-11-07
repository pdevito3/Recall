interface ProseNode {
  type?: unknown
  text?: unknown
  content?: unknown
  attrs?: { label?: unknown; start?: unknown; checked?: unknown; language?: unknown }
  marks?: unknown
}

interface ProseMark {
  type?: unknown
  attrs?: { href?: unknown }
}

function marksOf(node: ProseNode | undefined): ProseMark[] {
  return Array.isArray(node?.marks) ? (node.marks as ProseMark[]) : []
}

function addressOf(node: ProseNode | undefined): string | undefined {
  const href = marksOf(node).find((mark) => mark.type === 'link')?.attrs?.href
  return typeof href === 'string' ? href : undefined
}

function isStruck(node: ProseNode | undefined): boolean {
  return marksOf(node).some((mark) => mark.type === 'strike')
}

function childrenOf(node: ProseNode): ProseNode[] {
  return Array.isArray(node.content) ? (node.content as ProseNode[]) : []
}

function inlineTextOf(node: ProseNode): string {
  if (typeof node.text === 'string') return node.text
  if (node.type === 'mention' && typeof node.attrs?.label === 'string') return node.attrs.label
  if (node.type === 'hardBreak') return '\n'
  return ''
}

/**
 * A card has no marks, so two of them stay in the words. A struck word keeps its "~~", because "I do not" and "I do ~~not~~"
 * say opposite things. A link keeps its address after its words, unless its words are the address.
 */
function inlinesOf(nodes: ProseNode[]): string {
  let shown = ''
  let linkWords = ''
  nodes.forEach((node, index) => {
    const before = nodes[index - 1]
    const after = nodes[index + 1]
    const text = inlineTextOf(node)
    const opensStrike = isStruck(node) && !isStruck(before)
    const closesStrike = isStruck(node) && !isStruck(after)
    shown += `${opensStrike ? '~~' : ''}${text}${closesStrike ? '~~' : ''}`

    const address = addressOf(node)
    if (address === undefined) return
    linkWords = addressOf(before) === address ? linkWords + text : text
    const endsLink = addressOf(after) !== address
    if (endsLink && linkWords !== address && address !== `mailto:${linkWords}`) shown += ` (${address})`
  })
  return shown
}

function withPrefix(text: string, first: string): string {
  const rest = ' '.repeat(first.length)
  return text
    .split('\n')
    .map((line, index) => (index === 0 ? first : line ? rest : '') + line)
    .join('\n')
}

function itemsOf(list: ProseNode): string {
  const start = typeof list.attrs?.start === 'number' ? list.attrs.start : 1
  return childrenOf(list)
    .map((item, index) => {
      const words = childrenOf(item).map(blockOf).join('\n')
      if (list.type === 'orderedList') return withPrefix(words, `${start + index}. `)
      if (list.type === 'taskList') return withPrefix(words, item.attrs?.checked === true ? '[x] ' : '[ ] ')
      return withPrefix(words, '- ')
    })
    .join('\n')
}

function blockOf(node: ProseNode): string {
  switch (node.type) {
    case 'bulletList':
    case 'orderedList':
    case 'taskList':
      return itemsOf(node)
    case 'blockquote':
      return blocksOf(node)
        .split('\n')
        .map((line) => (line ? `> ${line}` : '>'))
        .join('\n')
    case 'codeBlock': {
      const language = typeof node.attrs?.language === 'string' ? node.attrs.language : ''
      return `\`\`\`${language}\n${inlinesOf(childrenOf(node))}\n\`\`\``
    }
    case 'horizontalRule':
      return '---'
    case 'paragraph':
    case 'heading':
      return inlinesOf(childrenOf(node))
    default:
      return blocksOf(node)
  }
}

function blocksOf(node: ProseNode): string {
  return childrenOf(node).map(blockOf).join('\n\n')
}

/** A Revision holds a ProseMirror document or plain words, and each form shows as words with what they mean: the sign of an item, its number, and the state of a task. */
export function plainTextOf(stored: string): string {
  try {
    const parsed: unknown = JSON.parse(stored)
    if (typeof parsed === 'object' && parsed !== null && (parsed as ProseNode).type === 'doc') {
      return blocksOf(parsed as ProseNode)
    }
  } catch {
    return stored
  }
  return stored
}
