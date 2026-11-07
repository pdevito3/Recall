import { createFileRoute } from '@tanstack/react-router'
import * as z from 'zod'

import { summaryDtoSchema } from '@/api/generated/models/zod/summary-dto'
import { SummariesPage } from '@/domain/summaries/components/summaries-page'

const searchSchema = z.object({
  period: summaryDtoSchema.shape.period.optional().catch(undefined),
  date: summaryDtoSchema.shape.periodDate.optional().catch(undefined),
})

export const Route = createFileRoute('/summaries')({
  validateSearch: searchSchema,
  component: SummariesRoute,
  staticData: { breadcrumb: 'Summaries' },
})

function SummariesRoute() {
  const { period = 'Day', date } = Route.useSearch()
  const navigate = Route.useNavigate()
  return <SummariesPage period={period} date={date} onPeriodChange={(next) => navigate({ search: (prev) => ({ ...prev, period: next }) })} />
}
