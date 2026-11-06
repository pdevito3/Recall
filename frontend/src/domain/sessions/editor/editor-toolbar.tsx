import {
  CheckListIcon,
  HeadingIcon,
  HighlighterIcon,
  LeftToRightListBulletIcon,
  LeftToRightListNumberIcon,
  QuoteUpIcon,
  TextBoldIcon,
  TextItalicIcon,
} from '@hugeicons/core-free-icons'
import { HugeiconsIcon, type IconSvgElement } from '@hugeicons/react'
import { useEditorState, type Editor } from '@tiptap/react'
import { ToggleButton, Toolbar } from 'react-aria-components'

interface Tool {
  label: string
  icon: IconSvgElement
  isActive: (editor: Editor) => boolean
  toggle: (editor: Editor) => void
}

const tools: Tool[] = [
  {
    label: 'Bold',
    icon: TextBoldIcon,
    isActive: (editor) => editor.isActive('bold'),
    toggle: (editor) => editor.chain().focus().toggleBold().run(),
  },
  {
    label: 'Italic',
    icon: TextItalicIcon,
    isActive: (editor) => editor.isActive('italic'),
    toggle: (editor) => editor.chain().focus().toggleItalic().run(),
  },
  {
    label: 'Highlight',
    icon: HighlighterIcon,
    isActive: (editor) => editor.isActive('highlight'),
    toggle: (editor) => editor.chain().focus().toggleHighlight().run(),
  },
  {
    label: 'Heading',
    icon: HeadingIcon,
    isActive: (editor) => editor.isActive('heading', { level: 2 }),
    toggle: (editor) => editor.chain().focus().toggleHeading({ level: 2 }).run(),
  },
  {
    label: 'Bulleted list',
    icon: LeftToRightListBulletIcon,
    isActive: (editor) => editor.isActive('bulletList'),
    toggle: (editor) => editor.chain().focus().toggleBulletList().run(),
  },
  {
    label: 'Numbered list',
    icon: LeftToRightListNumberIcon,
    isActive: (editor) => editor.isActive('orderedList'),
    toggle: (editor) => editor.chain().focus().toggleOrderedList().run(),
  },
  {
    label: 'Task list',
    icon: CheckListIcon,
    isActive: (editor) => editor.isActive('taskList'),
    toggle: (editor) => editor.chain().focus().toggleTaskList().run(),
  },
  {
    label: 'Quote',
    icon: QuoteUpIcon,
    isActive: (editor) => editor.isActive('blockquote'),
    toggle: (editor) => editor.chain().focus().toggleBlockquote().run(),
  },
]

export function EditorToolbar({ editor }: { editor: Editor }) {
  const active = useEditorState({
    editor,
    selector: ({ editor: current }) => tools.map((tool) => tool.isActive(current)),
    equalityFn: (a, b) => a.every((value, index) => value === b[index]),
  })

  return (
    <Toolbar aria-label="Formatting" className="flex flex-wrap gap-0.5 border-b p-1">
      {tools.map((tool, index) => (
        <ToggleButton
          key={tool.label}
          aria-label={tool.label}
          isSelected={active[index]}
          onChange={() => tool.toggle(editor)}
          className="inline-flex size-8 items-center justify-center rounded-md text-muted-foreground outline-none hover:bg-muted hover:text-foreground data-[focus-visible]:ring-[3px] data-[focus-visible]:ring-ring/50 data-[selected]:bg-accent data-[selected]:text-accent-foreground max-md:size-11"
        >
          <HugeiconsIcon icon={tool.icon} className="size-4" />
        </ToggleButton>
      ))}
    </Toolbar>
  )
}
