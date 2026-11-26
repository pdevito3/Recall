import { DOMSerializer } from '@tiptap/pm/model'
import { useLayoutEffect, useRef } from 'react'

import { contentOf, journalSchema } from './content-of'

const serializer = DOMSerializer.fromSchema(journalSchema)

export function JournalStaticText({ text }: { text: string }) {
  const box = useRef<HTMLDivElement>(null)
  useLayoutEffect(() => {
    box.current?.replaceChildren(serializer.serializeFragment(journalSchema.nodeFromJSON(contentOf(text)).content))
  }, [text])
  return <div ref={box} dir="auto" className="journal-prose" />
}
