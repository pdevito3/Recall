import { EditorState, Selection } from '@tiptap/pm/state'
import { Placeholder } from '@tiptap/extensions'
import { EditorContent, Extension, useEditor, type Editor } from '@tiptap/react'
import { useEffect, useLayoutEffect, useMemo, useRef, type ReactNode } from 'react'

import { useAddPerson, usePeopleDirectory } from '@/domain/people/apis/people'

import { contentOf } from './content-of'
import { PersonMention, textExtensions } from './journal-extensions'
import { mentionSuggestion, type MentionSource } from './mention-suggestion'

// The status bar sticks to the bottom of the screen, so the line with the cursor must stay above it.
const stickyBarRoom = { top: 0, right: 0, bottom: 80, left: 0 }

interface JournalEditorProps {
  initialText: string
  /** A new value opens `initialText` in this editor, as a new document with no undo. The editor stays mounted, so a key at this moment reaches it. */
  revision: number
  label: string
  placeholder?: string
  autoFocus?: boolean
  onChange: (text: string, isEmpty: boolean) => void
  onSubmit?: () => void
  toolbar?: (editor: Editor) => ReactNode
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

export function JournalEditor({ initialText, revision, label, placeholder, autoFocus, onChange, onSubmit, toolbar }: JournalEditorProps) {
  const mentionSource = useMentionSource()
  const callbacks = useRef({ onChange, onSubmit, placeholder })
  useLayoutEffect(() => {
    callbacks.current = { onChange, onSubmit, placeholder }
  }, [onChange, onSubmit, placeholder])

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
      scrollThreshold: stickyBarRoom,
      scrollMargin: stickyBarRoom,
      attributes: {
        role: 'textbox',
        'aria-multiline': 'true',
        'aria-label': label,
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
    editor.view.updateState(EditorState.create({ doc, plugins, selection: Selection.atEnd(doc) }))
    if (autoFocus) editor.view.focus()
  }, [editor, revision, initialText, autoFocus])

  return (
    <div className="flex flex-col rounded-lg border bg-card focus-within:border-ring focus-within:ring-[3px] focus-within:ring-ring/50">
      {editor && toolbar?.(editor)}
      <EditorContent editor={editor} />
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
    editorProps: { attributes: { 'aria-label': label, dir: 'auto', class: 'journal-prose outline-none' } },
  })
  return <EditorContent editor={editor} />
}
