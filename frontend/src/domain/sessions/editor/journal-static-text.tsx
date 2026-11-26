import { DOMSerializer } from '@tiptap/pm/model'
import { useLayoutEffect, useRef } from 'react'

import { contentOf, journalSchema } from './content-of'

const serializer = DOMSerializer.fromSchema(journalSchema)

/** The words of a journal document as static text. A task item shows its check box, which is off and is named by the words of the item. */
export function JournalStaticText({ text }: { text: string }) {
  const box = useRef<HTMLDivElement>(null)
  useLayoutEffect(() => {
    const words = serializer.serializeFragment(journalSchema.nodeFromJSON(contentOf(text)).content)
    for (const check of words.querySelectorAll<HTMLInputElement>('li[data-type="taskItem"] > label > input[type="checkbox"]')) {
      check.disabled = true
      const item = check.closest('li')?.querySelector(':scope > div > :first-child')?.textContent?.trim()
      if (item) check.setAttribute('aria-label', item)
    }
    box.current?.replaceChildren(words)
  }, [text])
  return <div ref={box} dir="auto" className="journal-prose" />
}
