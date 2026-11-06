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
  const isEmptyDocument = document?.content?.length === 0 && Object.keys(document).length === 2
  const words = plainWordsOf(text).trim() || isEmptyDocument ? plainWordsOf(text) : text
  return document && fitsTheEditor(document) ? document : {
    type: 'doc',
    content: words.split('\n').map((line) => (line ? { type: 'paragraph', content: [{ type: 'text', text: line }] } : { type: 'paragraph' })),
  }
}
