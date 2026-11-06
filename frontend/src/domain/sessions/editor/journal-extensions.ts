import Highlight from '@tiptap/extension-highlight'
import { TaskItem, TaskList } from '@tiptap/extension-list'
import Mention from '@tiptap/extension-mention'
import type { Extensions } from '@tiptap/react'
import StarterKit from '@tiptap/starter-kit'

export const PersonMention = Mention.extend({
  addAttributes() {
    return {
      personId: {
        default: null,
        parseHTML: (element) => element.getAttribute('data-person-id'),
        renderHTML: (attributes) => (attributes.personId ? { 'data-person-id': attributes.personId as string } : {}),
      },
      label: {
        default: null,
        parseHTML: (element) => element.getAttribute('data-label'),
        renderHTML: (attributes) => (attributes.label ? { 'data-label': attributes.label as string } : {}),
      },
    }
  },
  renderHTML({ node, HTMLAttributes }) {
    return ['span', { ...HTMLAttributes, class: 'journal-mention' }, `@${String(node.attrs.label ?? '')}`]
  },
  renderText({ node }) {
    return `@${String(node.attrs.label ?? '')}`
  },
})

export const textExtensions: Extensions = [
  StarterKit.configure({ heading: { levels: [1, 2, 3] }, link: { openOnClick: false } }),
  Highlight,
  TaskList,
  TaskItem.configure({ nested: true }),
]
