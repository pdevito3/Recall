import { createFileRoute } from '@tanstack/react-router'

import { ModelProvidersPage } from '@/domain/model-providers/components/model-providers-page'

export const Route = createFileRoute('/model-providers')({
  component: ModelProvidersPage,
  staticData: { breadcrumb: 'Model providers' },
})
