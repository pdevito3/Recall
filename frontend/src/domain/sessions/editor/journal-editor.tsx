import { Placeholder } from '@tiptap/extensions'
import { EditorContent, Extension, useEditor, type Editor } from '@tiptap/react'
import { useEffect, useMemo, useRef, type ReactNode } from 'react'

import { useAddPerson, usePeopleDirectory } from '@/domain/people/apis/people'

import { contentOf } from './content'
import { PersonMention, textExtensions } from './journal-extensions'
import { mentionSuggestion, type MentionSource } from './mention-suggestion'

interface JournalEditorProps {
  /** The editor reads this once. To show other words, give the component another `key`. */
  initialText: string
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

export function JournalEditor({ initialText, label, placeholder, autoFocus, onChange, onSubmit, toolbar }: JournalEditorProps) {
  const mentionSource = useMentionSource()
  const callbacks = useRef({ onChange, onSubmit })
  useEffect(() => {
    callbacks.current = { onChange, onSubmit }
  }, [onChange, onSubmit])

  const editor = useEditor({
    autofocus: autoFocus ? 'end' : false,
    content: contentOf(initialText),
    extensions: [
      ...textExtensions,
      PersonMention.configure({ suggestion: mentionSuggestion(mentionSource) }),
      Placeholder.configure({ placeholder: placeholder ?? '' }),
      SubmitKey.configure({ onSubmit: () => callbacks.current.onSubmit?.() }),
      LeaveKey,
    ],
    editorProps: {
      attributes: {
        role: 'textbox',
        'aria-multiline': 'true',
        'aria-label': label,
        class: 'journal-prose min-h-32 px-3 py-2 outline-none',
      },
    },
    onUpdate: ({ editor: changed }) => callbacks.current.onChange(JSON.stringify(changed.getJSON()), changed.isEmpty),
  })

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

/** Shows a saved text with no editing. */
export function JournalText({ text, label }: { text: string; label: string }) {
  const editor = useEditor({
    editable: false,
    content: contentOf(text),
    extensions: [...textExtensions, PersonMention],
    editorProps: { attributes: { 'aria-label': label, class: 'journal-prose outline-none' } },
  })
  return <EditorContent editor={editor} />
}
