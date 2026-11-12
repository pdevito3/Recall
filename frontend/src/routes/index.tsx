import { createFileRoute } from '@tanstack/react-router'

import { Page } from '@/components/page'
import { PageHeading } from '@/components/page-heading'
import { JobsStoppedNotice } from '@/domain/capture/components/jobs-stopped-notice'
import { MicCard } from '@/domain/capture/components/mic-card'
import { ComposerForm } from '@/domain/sessions/components/composer'
import { Timeline } from '@/domain/sessions/components/timeline'
import { useComposer } from '@/domain/sessions/components/use-composer'
import { useReportDeviceTimeZone } from '@/domain/settings/apis/use-report-device-time-zone'
import { useJournalZone } from '@/domain/settings/apis/user-setting'
import { formatDate, todayAt } from '@/lib/zoned-time'

export const Route = createFileRoute('/')({
  component: Journal,
  staticData: { breadcrumb: 'Journal' },
})

function Journal() {
  useReportDeviceTimeZone()
  const zone = useJournalZone()
  const composer = useComposer()
  const today = formatDate(todayAt(zone, new Date()), { weekday: 'long', month: 'long', day: 'numeric' })

  return (
    <Page>
      <JobsStoppedNotice />
      <PageHeading>{today}</PageHeading>
      <MicCard />
      <ComposerForm composer={composer} />
      <Timeline zone={zone} hiddenSessionId={composer.composingSessionId} />
    </Page>
  )
}
