import { createFileRoute } from '@tanstack/react-router'
import * as z from 'zod'

import { ModelProvidersPage } from '@/domain/model-providers/components/model-providers-page'

const searchSchema = z.object({
  session: z.string().max(64).optional().catch(undefined),
})

export const Route = createFileRoute('/model-providers')({
  validateSearch: searchSchema,
  component: ModelProvidersRoute,
  staticData: { breadcrumb: 'Model providers' },
})

function ModelProvidersRoute() {
  const { session } = Route.useSearch()
  return <ModelProvidersPage sessionId={session} />
}
