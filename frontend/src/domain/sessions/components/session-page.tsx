import { useState } from 'react'
import { Tab, TabList, Tabs } from 'react-aria-components'

import type { SessionDto } from '@/api/generated/models/types/session-dto'
import { useBreadcrumbName } from '@/components/breadcrumb-name'
import { PageHeading } from '@/components/page-heading'
import { useJournalZone } from '@/domain/settings/apis/user-setting'
import { formatInstant, type TimeZone } from '@/lib/zoned-time'

import type { DraftChannel } from '../apis/session.keys'
import { DeleteSession } from './delete-session'
import { DraftEditor } from './draft-editor'
import { SessionTags } from './session-tags'

const cleanupLabels: Record<SessionDto['cleanupStatus'], string | null> = {
  NotRun: null,
  Running: 'Cleanup is running.',
  Clean: 'The cleaned version is up to date.',
  Stale: 'Your words changed after the last cleanup.',
  Failed: 'The last cleanup did not finish. Your words did not change.',
}

const tabClass =
  'flex min-h-9 items-center rounded-md px-3 text-sm outline-none data-[focus-visible]:ring-[3px] data-[focus-visible]:ring-ring/50 data-[selected]:bg-background data-[selected]:shadow-sm max-md:min-h-11'

function headingFor(session: SessionDto, zone: TimeZone) {
  return formatInstant(new Date(session.startedAt), zone, {
    weekday: 'long',
    month: 'long',
    day: 'numeric',
    hour: 'numeric',
    minute: '2-digit',
  })
}

export function SessionPage({ session }: { session: SessionDto }) {
  const zone = useJournalZone()
  const [channel, setChannel] = useState<DraftChannel>('raw')
  const hasCleaned = session.cleanedLatestRevision > 0
  const cleanup = cleanupLabels[session.cleanupStatus]
  const heading = headingFor(session, zone)
  useBreadcrumbName(heading)

  return (
    <>
      <div className="flex flex-wrap items-start justify-between gap-3">
        <PageHeading className="min-w-0 flex-1 basis-64">{heading}</PageHeading>
        <DeleteSession session={session} />
      </div>
      {session.synopsis && <p className="text-muted-foreground wrap-anywhere">{session.synopsis}</p>}
      {cleanup && <p className="text-sm text-muted-foreground">{cleanup}</p>}
      {hasCleaned && (
        <Tabs selectedKey={channel} onSelectionChange={(key) => setChannel(key as DraftChannel)}>
          <TabList aria-label="Versions of this entry" className="inline-flex gap-1 rounded-lg bg-muted p-1">
            <Tab id="raw" className={tabClass}>Your words</Tab>
            <Tab id="cleaned" className={tabClass}>Cleaned</Tab>
          </TabList>
        </Tabs>
      )}
      <DraftEditor key={channel} session={session} channel={channel} />
      <SessionTags session={session} />
    </>
  )
}
