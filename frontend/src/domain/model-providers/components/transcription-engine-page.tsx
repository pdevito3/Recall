import { Page } from '@/components/page'
import { PageHeading } from '@/components/page-heading'
import { useCanManageModelProviders } from '@/domain/membership/apis/get-membership'

import { TranscriptionEngineCard } from './transcription-engine-card'

export function TranscriptionEnginePage() {
  const canManage = useCanManageModelProviders()

  return (
    <Page>
      <PageHeading>Transcription</PageHeading>
      <TranscriptionEngineCard canChoose={canManage === true} />
    </Page>
  )
}
