import { useLayoutEffect, useRef } from 'react'

import type { EditingPlace } from '../autosave/entry-lock'

/** The editor left the page when another tab took the entry. A focus that it took with it goes to Edit here, so a keyboard keeps its place. */
export function useFocusEditHereWhenTheEditorLeaves(editing: EditingPlace) {
  const editHere = useRef<HTMLButtonElement>(null)
  const placeBefore = useRef(editing)
  useLayoutEffect(() => {
    const before = placeBefore.current
    placeBefore.current = editing
    const isFocusLost = document.activeElement === null || document.activeElement === document.body
    if (editing === 'elsewhere' && before === 'here' && isFocusLost) editHere.current?.focus()
  }, [editing])
  return editHere
}
