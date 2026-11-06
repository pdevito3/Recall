import { Alert02Icon, CheckmarkCircle02Icon, Clock01Icon } from '@hugeicons/core-free-icons'
import { HugeiconsIcon, type IconSvgElement } from '@hugeicons/react'

import { formatInstant, type TimeZone } from '@/lib/zoned-time'

import type { ShownSaveStatus } from '../autosave/draft-saver'

interface SaveStatusLineProps {
  status: ShownSaveStatus
  zone: TimeZone
}

interface Shown {
  icon?: IconSvgElement
  text: string
  tone: string
}

const deletedEntryText = 'Not saved. This entry was deleted in another tab or device.'

const timeOf = (at: string, zone: TimeZone) => formatInstant(new Date(at), zone, { hour: 'numeric', minute: '2-digit' })

function shownFor(status: ShownSaveStatus, zone: TimeZone): Shown | null {
  switch (status.kind) {
    case 'idle':
      return null
    case 'unsent':
      return { text: 'Not saved yet', tone: 'text-muted-foreground' }
    case 'saving':
      return { text: 'Saving', tone: 'text-muted-foreground' }
    case 'waiting':
      return {
        icon: Clock01Icon,
        text: 'Not saved yet. The save waits for a connection to the server. Keep this page open.',
        tone: 'text-foreground',
      }
    case 'saved':
      return {
        icon: CheckmarkCircle02Icon,
        text: `Saved at ${timeOf(status.at, zone)}`,
        tone: 'text-muted-foreground',
      }
    case 'savedInHistory':
      return { icon: Clock01Icon, text: `Saved in History at ${timeOf(status.at, zone)}`, tone: 'text-foreground' }
    case 'refused':
      return { icon: Alert02Icon, text: status.isGone ? deletedEntryText : `Not saved. ${status.message}`, tone: 'text-destructive' }
  }
}

export function SaveStatusLine({ status, zone }: SaveStatusLineProps) {
  const shown = shownFor(status, zone)
  return (
    <p role="status" className={`flex min-h-5 min-w-0 items-start gap-1.5 text-sm wrap-anywhere ${shown?.tone ?? ''}`}>
      {shown?.icon && <HugeiconsIcon icon={shown.icon} className="mt-0.5 size-4 shrink-0" />}
      {shown && <span>{shown.text}</span>}
    </p>
  )
}
