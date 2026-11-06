import { getSchema, type JSONContent } from '@tiptap/react'

import { parseDocument } from './content'
import { PersonMention, textExtensions } from './journal-extensions'

const schema = getSchema([...textExtensions, PersonMention])

function fitsTheEditor(document: JSONContent): boolean {
  try {
    schema.nodeFromJSON(document).check()
    return true
  } catch {
    return false
  }
}

export function contentOf(text: string): JSONContent {
  const document = parseDocument(text)
  return document && fitsTheEditor(document) ? document : {
    type: 'doc',
    content: text.split('\n').map((line) => (line ? { type: 'paragraph', content: [{ type: 'text', text: line }] } : { type: 'paragraph' })),
  }
}
