import { DOMSerializer } from '@tiptap/pm/model'
import { useLayoutEffect, useRef } from 'react'

import { contentOf, journalSchema } from './content-of'

const serializer = DOMSerializer.fromSchema(journalSchema)

/**
 * The words of a document as the read-only editor shows them, with no editor. A page of many entries mounts one for
 * each, and an editor for each made a page of 200 entries take seconds to show.
 */
export function JournalStaticText({ text }: { text: string }) {
  const box = useRef<HTMLDivElement>(null)
  useLayoutEffect(() => {
    box.current?.replaceChildren(serializer.serializeFragment(journalSchema.nodeFromJSON(contentOf(text)).content))
  }, [text])
  return <div ref={box} dir="auto" className="journal-prose" />
}
