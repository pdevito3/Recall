import { Page } from '@/components/page'
import { PageHeading } from '@/components/page-heading'
import { useMembership } from '@/domain/membership/apis/get-membership'

import { TranscriptionEngineCard } from './transcription-engine-card'

export function TranscriptionEnginePage() {
  const membership = useMembership()
  const mightBeAnAdmin = membership.status === 'member' && membership.user.role === 'Admin'

  return (
    <Page>
      <PageHeading>Transcription</PageHeading>
      <TranscriptionEngineCard canChoose={mightBeAnAdmin} />
    </Page>
  )
}
