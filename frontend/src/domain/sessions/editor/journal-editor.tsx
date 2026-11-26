import { EditorState, Selection } from '@tiptap/pm/state'
import { Placeholder } from '@tiptap/extensions'
import { EditorContent, Extension, useEditor, type Editor } from '@tiptap/react'
import { useEffect, useLayoutEffect, useMemo, useRef, useState, type ReactNode, type RefObject } from 'react'

import { useAddPerson, usePeopleDirectory } from '@/domain/people/apis/people'
import { cn } from '@/lib/utils'

import { contentOf } from './content-of'
import { PersonMention, textExtensions } from './journal-extensions'
import { mentionSuggestion, type MentionSource } from './mention-suggestion'

const gapAboveBar = 8

/** The bar under the editor holds the status, and its text can wrap to several lines, so its height changes. */
function useHeightOf(ref: RefObject<HTMLElement | null> | undefined): number {
  const [height, setHeight] = useState(0)
  // Not a layout effect: the bar renders after the editor, and React sets its ref after the layout effects of the editor.
  useEffect(() => {
    const element = ref?.current
    if (!element) return
    const observer = new ResizeObserver(() => setHeight(element.getBoundingClientRect().height))
    observer.observe(element, { box: 'border-box' })
    return () => observer.disconnect()
  }, [ref])
  return height
}

interface JournalEditorProps {
  initialText: string
  /** A new value opens `initialText` in this editor, as a new document with no undo. The editor stays mounted, so a key at this moment reaches it. */
  revision: number
  label: string
  /** The ID of the text box, so a form can move the focus to it. */
  id?: string
  /** The ID of the message of a refused value. The box then shows as invalid and names the message. */
  errorId?: string
  placeholder?: string
  autoFocus?: boolean
  /** A text that the Server refused can be very long, so it scrolls in its box and the page stays usable. */
  isBounded?: boolean
  onChange: (text: string, isEmpty: boolean) => void
  onSubmit?: () => void
  toolbar?: (editor: Editor) => ReactNode
  /** The bar that sticks to the bottom of the screen under the editor. The line with the cursor stays above it. */
  barRef?: RefObject<HTMLElement | null>
}

function useMentionSource(): MentionSource {
  const directory = usePeopleDirectory().data
  const addPerson = useAddPerson().mutateAsync
  const latest = useRef({ directory, addPerson })
  useEffect(() => {
    latest.current = { directory, addPerson }
  }, [directory, addPerson])
  return useMemo(
    () => ({
      people: () => latest.current.directory ?? [],
      addPerson: (label) => latest.current.addPerson(label),
    }),
    [],
  )
}

export function JournalEditor({ initialText, revision, label, id, errorId, placeholder, autoFocus, isBounded, onChange, onSubmit, toolbar, barRef }: JournalEditorProps) {
  const mentionSource = useMentionSource()
  const callbacks = useRef({ onChange, onSubmit, placeholder })
  useLayoutEffect(() => {
    callbacks.current = { onChange, onSubmit, placeholder }
  }, [onChange, onSubmit, placeholder])
  const barHeight = useHeightOf(barRef)
  const barRoom = { top: 0, right: 0, bottom: barHeight + gapAboveBar, left: 0 }

  const editor = useEditor({
    autofocus: autoFocus ? 'end' : false,
    content: contentOf(initialText),
    extensions: [
      ...textExtensions,
      PersonMention.configure({ suggestion: mentionSuggestion(mentionSource) }),
      Placeholder.configure({ placeholder: () => callbacks.current.placeholder ?? '' }),
      SubmitKey.configure({ onSubmit: () => callbacks.current.onSubmit?.() }),
      LeaveKey,
    ],
    editorProps: {
      scrollThreshold: barRoom,
      scrollMargin: barRoom,
      attributes: {
        role: 'textbox',
        'aria-multiline': 'true',
        'aria-label': label,
        ...(id && { id }),
        ...(errorId && { 'aria-invalid': 'true', 'aria-describedby': errorId }),
        dir: 'auto',
        class: 'journal-prose min-h-32 px-3 py-2 outline-none',
      },
    },
    onUpdate: ({ editor: changed }) => callbacks.current.onChange(JSON.stringify(changed.getJSON()), changed.isEmpty),
  })

  const openedRevision = useRef(revision)
  useLayoutEffect(() => {
    if (!editor || openedRevision.current === revision) return
    openedRevision.current = revision
    const { schema, plugins } = editor.state
    const doc = schema.nodeFromJSON(contentOf(initialText))
    const isWriting = editor.view.hasFocus()
    editor.view.updateState(EditorState.create({ doc, plugins, selection: Selection.atEnd(doc) }))
    if (autoFocus) editor.view.focus()
    // The new words can be longer, and the cursor moves to their end, so a person who writes keeps the cursor in view.
    if (isWriting) editor.commands.scrollIntoView()
  }, [editor, revision, initialText, autoFocus])

  // A status that wraps to more lines makes the bar taller with no key pressed, and the bar then covers the line with the cursor.
  // A cursor off the screen stays there, so a person who scrolled away to read stays where they read. The bound of a
  // refused text scrolls the cursor out of its box, so the box then scrolls to it.
  useEffect(() => {
    if (!editor?.isFocused) return
    const cursor = editor.view.coordsAtPos(editor.state.selection.head)
    if (isBounded || (cursor.bottom > 0 && cursor.top < window.innerHeight)) editor.commands.scrollIntoView()
  }, [editor, barHeight, isBounded])

  return (
    <div
      className={cn(
        'flex flex-col rounded-lg border bg-card focus-within:border-ring focus-within:ring-[3px] focus-within:ring-ring/50',
        errorId && 'border-destructive focus-within:border-destructive focus-within:ring-destructive/20 dark:focus-within:ring-destructive/40',
      )}
    >
      {editor && toolbar?.(editor)}
      <EditorContent editor={editor} className={isBounded ? 'max-h-[60svh] overflow-y-auto' : undefined} />
    </div>
  )
}

/** A high priority, because a hard break of the StarterKit also binds this key. Shift-Enter still makes a hard break. */
const SubmitKey = Extension.create<{ onSubmit: () => void }>({
  name: 'journalSubmitKey',
  priority: 200,
  addKeyboardShortcuts() {
    return {
      'Mod-Enter': () => {
        this.options.onSubmit()
        return true
      },
    }
  },
})

/**
 * Tab moves a list item inside a list, so this is how a keyboard leaves the editor there.
 * A low priority, so that an open list of people gets the Escape key first.
 */
const LeaveKey = Extension.create({
  name: 'journalLeaveKey',
  priority: 50,
  addKeyboardShortcuts() {
    return { Escape: () => this.editor.commands.blur() }
  },
})

export function JournalText({ text, label }: { text: string; label: string }) {
  const editor = useEditor({
    editable: false,
    content: contentOf(text),
    extensions: [...textExtensions, PersonMention],
    editorProps: { attributes: { 'aria-label': label, 'aria-readonly': 'true', dir: 'auto', class: 'journal-prose outline-none' } },
  })
  return <EditorContent editor={editor} />
}
