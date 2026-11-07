import { PageHeading } from '@/components/page-heading'
import { useMembership } from '@/domain/membership/apis/get-membership'

import { TranscriptionEngineCard } from './transcription-engine-card'

export function TranscriptionEnginePage() {
  const membership = useMembership()
  const mightBeAnAdmin = membership.status === 'member' && membership.user.role === 'Admin'

  return (
    <div className="mx-auto flex w-full max-w-4xl flex-col gap-6 px-4 py-8 max-sm:[&_[data-slot=button]]:h-11 max-sm:[&_[data-slot=button]]:min-w-11 max-sm:[&_[data-slot=input]]:h-11 max-sm:[&_[data-slot=select-trigger]]:h-11">
      <PageHeading>Transcription</PageHeading>
      <TranscriptionEngineCard canChoose={mightBeAnAdmin} />
    </div>
  )
}
