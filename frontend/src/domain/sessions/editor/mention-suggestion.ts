import { ReactRenderer, type Editor } from '@tiptap/react'
import type { SuggestionOptions, SuggestionProps } from '@tiptap/suggestion'

import { MentionList, type MentionListHandle, type MentionOption } from './mention-list'

export interface MentionSource {
  people: () => { id: string; label: string }[]
  addPerson: (label: string) => Promise<{ id: string; label: string }>
}

const shownOptions = 8
const gapPx = 4
const edgePx = 8

export function optionsFor(people: { id: string; label: string }[], query: string): MentionOption[] {
  const wanted = query.trim().toLowerCase()
  const matches = people.filter((person) => person.label.toLowerCase().includes(wanted)).slice(0, shownOptions)
  const options: MentionOption[] = matches.map((person) => ({ kind: 'person', personId: person.id, label: person.label }))
  const hasExactMatch = people.some((person) => person.label.toLowerCase() === wanted)
  return wanted && !hasExactMatch ? [...options, { kind: 'create', label: query.trim() }] : options
}

function place(popup: HTMLElement, caret: DOMRect | null | undefined) {
  if (!caret) return
  // The on-screen keyboard of a phone covers the bottom of the layout viewport, and only the visual viewport knows it.
  const viewport = window.visualViewport
  const height = viewport?.height ?? window.innerHeight
  const width = viewport?.width ?? window.innerWidth
  const box = popup.getBoundingClientRect()
  const below = caret.bottom + gapPx
  const top = below + box.height > height - edgePx ? Math.max(edgePx, caret.top - gapPx - box.height) : below
  const left = Math.max(edgePx, Math.min(caret.left, width - box.width - edgePx))
  popup.style.top = `${top}px`
  popup.style.left = `${left}px`
}

function insertMention(editor: Editor, range: { from: number; to: number }, personId: string, label: string) {
  editor
    .chain()
    .focus()
    .insertContentAt(range, [
      { type: 'mention', attrs: { personId, label } },
      { type: 'text', text: ' ' },
    ])
    .run()
}

export function mentionSuggestion(source: MentionSource): Omit<SuggestionOptions<MentionOption, MentionOption>, 'editor'> {
  return {
    char: '@',
    items: ({ query }) => optionsFor(source.people(), query),
    command: ({ editor, range, props }) => {
      if (props.kind === 'person') {
        insertMention(editor, range, props.personId, props.label)
        return
      }
      void source.addPerson(props.label).then((person) => insertMention(editor, range, person.id, person.label))
    },
    render: () => {
      let list: ReactRenderer<MentionListHandle> | undefined
      const update = (props: SuggestionProps<MentionOption, MentionOption>) => {
        list?.updateProps(props)
        if (list) place(list.element as HTMLElement, props.clientRect?.())
      }
      return {
        onStart: (props) => {
          list = new ReactRenderer(MentionList, { props, editor: props.editor })
          const popup = list.element as HTMLElement
          popup.style.position = 'fixed'
          popup.style.zIndex = '50'
          document.body.append(popup)
          place(popup, props.clientRect?.())
        },
        onUpdate: update,
        onKeyDown: ({ event }) => {
          if (event.key === 'Escape') {
            list?.destroy()
            list?.element.remove()
            list = undefined
            return true
          }
          return list?.ref?.onKeyDown(event) ?? false
        },
        onExit: () => {
          list?.destroy()
          list?.element.remove()
          list = undefined
        },
      }
    },
  }
}
