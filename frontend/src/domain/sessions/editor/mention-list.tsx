import { useImperativeHandle, useState, type Ref } from 'react'

import { cn } from '@/lib/utils'

export type MentionOption = { kind: 'person'; personId: string; label: string; fullName?: string | null } | { kind: 'create'; label: string }

export interface MentionListHandle {
  onKeyDown: (event: KeyboardEvent) => boolean
}

interface MentionListProps {
  items: MentionOption[]
  command: (option: MentionOption) => void
  ref?: Ref<MentionListHandle>
}

export function MentionList({ items, command, ref }: MentionListProps) {
  const [selected, setSelected] = useState(0)
  const current = Math.min(selected, Math.max(items.length - 1, 0))

  useImperativeHandle(ref, () => ({
    onKeyDown(event) {
      if (items.length === 0) return false
      if (event.key === 'ArrowDown') {
        setSelected((current + 1) % items.length)
        return true
      }
      if (event.key === 'ArrowUp') {
        setSelected((current - 1 + items.length) % items.length)
        return true
      }
      if (event.key === 'Enter' || event.key === 'Tab') {
        command(items[current])
        return true
      }
      return false
    },
  }))

  if (items.length === 0) return null

  return (
    <div
      role="listbox"
      aria-label="People"
      className="max-h-60 w-64 max-w-[calc(100vw-2rem)] overflow-y-auto rounded-lg border bg-popover p-1 text-sm text-popover-foreground shadow-md"
    >
      {items.map((item, index) => (
        <button
          key={item.kind === 'person' ? item.personId : 'create'}
          type="button"
          role="option"
          aria-selected={index === current}
          className={cn(
            'flex min-h-9 w-full items-center rounded-md px-2 py-1 text-left wrap-anywhere max-md:min-h-11',
            index === current && 'bg-accent text-accent-foreground',
          )}
          onMouseDown={(event) => {
            event.preventDefault()
            command(item)
          }}
        >
          {item.kind === 'person' ? (
            <span className="flex min-w-0 flex-col">
              <span>{item.label}</span>
              {item.fullName && <span dir="auto" className="line-clamp-2 text-xs text-muted-foreground">{item.fullName}</span>}
            </span>
          ) : (
            `Add ${item.label} to your people`
          )}
        </button>
      ))}
    </div>
  )
}
