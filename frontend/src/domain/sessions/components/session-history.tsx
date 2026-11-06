import { useState } from 'react'

import type { RevisionSummaryDto } from '@/api/generated/models/types/revision-summary-dto'
import { QueryError } from '@/components/query-error'
import { Button } from '@/components/ui/button'
import {
  ResponsiveDialog,
  ResponsiveDialogClose,
  ResponsiveDialogFooter,
  ResponsiveDialogHeader,
  ResponsiveDialogTitle,
} from '@/components/ui/responsive-dialog'
import { useJournalZone } from '@/domain/settings/apis/user-setting'
import { formatInstant, type TimeZone } from '@/lib/zoned-time'

import { useRevision, useRevisionPages } from '../apis/revisions'
import type { DraftChannel } from '../apis/session.keys'
import { JournalText } from '../editor/journal-editor'

interface SessionHistoryProps {
  sessionId: string
  channel: DraftChannel
  currentRevision: number
  onRestore: (text: string) => void
}

const versionTime = (savedAt: string, zone: TimeZone) =>
  formatInstant(new Date(savedAt), zone, { month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit' })

export function SessionHistory({ sessionId, channel, currentRevision, onRestore }: SessionHistoryProps) {
  const [isOpen, setIsOpen] = useState(false)
  const [viewing, setViewing] = useState<RevisionSummaryDto | null>(null)
  const pages = useRevisionPages(sessionId, channel, { enabled: isOpen })
  const zone = useJournalZone()
  const revisions = pages.data?.pages.flatMap((page) => page.items) ?? []

  return (
    <section aria-labelledby="entry-history" className="flex flex-col gap-3">
      <div className="flex items-center justify-between gap-2">
        <h2 id="entry-history" className="text-lg font-semibold">History</h2>
        <Button variant="outline" aria-expanded={isOpen} onPress={() => setIsOpen(!isOpen)}>
          {isOpen ? 'Close history' : 'Show history'}
        </Button>
      </div>
      {isOpen && pages.isPending && <p role="status" className="text-sm text-muted-foreground">Loading earlier versions</p>}
      {isOpen && pages.isError && (
        <QueryError title="The earlier versions did not load." error={pages.error} onRetry={() => pages.refetch()} />
      )}
      {isOpen && pages.isSuccess && revisions.length === 0 && (
        <p className="text-sm text-muted-foreground">This entry has no saved versions yet.</p>
      )}
      {isOpen && revisions.length > 0 && (
        <ul aria-label="Saved versions" className="flex flex-col divide-y rounded-lg border">
          {revisions.map((revision) => (
            <li key={revision.number} className="flex items-center justify-between gap-3 p-2 pl-3">
              <span className="min-w-0 text-sm">
                {versionTime(revision.savedAt, zone)}
                {revision.number === currentRevision && <span className="text-muted-foreground"> (showing now)</span>}
              </span>
              <Button variant="ghost" onPress={() => setViewing(revision)} aria-label={`View version from ${versionTime(revision.savedAt, zone)}`}>
                View
              </Button>
            </li>
          ))}
        </ul>
      )}
      {pages.hasNextPage && (
        <Button variant="outline" isPending={pages.isFetchingNextPage} onPress={() => pages.fetchNextPage()}>
          Show older versions
        </Button>
      )}
      <ResponsiveDialog isOpen={viewing !== null} onOpenChange={(open) => !open && setViewing(null)}>
        {viewing && (
          <RevisionView
            sessionId={sessionId}
            channel={channel}
            revision={viewing}
            zone={zone}
            canRestore={viewing.number !== currentRevision}
            onRestore={(text) => {
              onRestore(text)
              setViewing(null)
            }}
          />
        )}
      </ResponsiveDialog>
    </section>
  )
}

interface RevisionViewProps {
  sessionId: string
  channel: DraftChannel
  revision: RevisionSummaryDto
  zone: TimeZone
  canRestore: boolean
  onRestore: (text: string) => void
}

function RevisionView({ sessionId, channel, revision, zone, canRestore, onRestore }: RevisionViewProps) {
  const full = useRevision(sessionId, channel, revision.number)
  return (
    <>
      <ResponsiveDialogHeader>
        <ResponsiveDialogTitle>Version from {versionTime(revision.savedAt, zone)}</ResponsiveDialogTitle>
      </ResponsiveDialogHeader>
      <div className="mt-4 max-h-[60svh] overflow-y-auto">
        {full.isPending && <p role="status" className="text-sm text-muted-foreground">Loading this version</p>}
        {full.isError && <QueryError title="This version did not load." error={full.error} onRetry={() => full.refetch()} />}
        {full.isSuccess && <JournalText text={full.data.text} label="Words of this version" />}
      </div>
      <ResponsiveDialogFooter>
        <ResponsiveDialogClose>Close</ResponsiveDialogClose>
        {canRestore && full.isSuccess && <Button onPress={() => onRestore(full.data.text)}>Restore this version</Button>}
      </ResponsiveDialogFooter>
    </>
  )
}
