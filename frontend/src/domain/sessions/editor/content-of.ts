import { getSchema, type JSONContent } from '@tiptap/react'

import { parseDocument, plainWordsOf } from './content'
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
  // A document with no words that the app can read shows its text, so that the words stay on the screen.
  const words = plainWordsOf(text).trim() ? plainWordsOf(text) : text
  return document && fitsTheEditor(document) ? document : {
    type: 'doc',
    content: words.split('\n').map((line) => (line ? { type: 'paragraph', content: [{ type: 'text', text: line }] } : { type: 'paragraph' })),
  }
}
