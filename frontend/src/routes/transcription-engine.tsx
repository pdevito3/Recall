import { createFileRoute } from '@tanstack/react-router'

import { TranscriptionEnginePage } from '@/domain/model-providers/components/transcription-engine-page'

export const Route = createFileRoute('/transcription-engine')({
  component: TranscriptionEnginePage,
  staticData: { breadcrumb: 'Audio to text' },
})
