import { createFileRoute } from '@tanstack/react-router'
import { useRef } from 'react'
import * as z from 'zod'

import { Page } from '@/components/page'
import { PageHeading } from '@/components/page-heading'
import { termMaxLength } from '@/domain/corrections/components/correction-form-schema'
import { CorrectionsList } from '@/domain/corrections/components/corrections-list'

const correctionsSearchSchema = z.object({
  search: z.string().max(termMaxLength).optional().catch(undefined),
})

export const Route = createFileRoute('/corrections/')({
  validateSearch: correctionsSearchSchema,
  component: CorrectionsRoute,
  staticData: { breadcrumb: 'Corrections' },
})

function CorrectionsRoute() {
  const { search = '' } = Route.useSearch()
  const navigate = Route.useNavigate()
  const headingRef = useRef<HTMLHeadingElement>(null)
  // A search that the user typed before Back does not replace the search that Back opened
  const handleSearch = (text: string, base: string) =>
    navigate({ search: (prev) => ((prev.search ?? '') === base ? { search: text || undefined } : prev), replace: true })

  return (
    <Page>
      <PageHeading ref={headingRef}>Corrections</PageHeading>
      <CorrectionsList search={search} onSearch={handleSearch} headingRef={headingRef} />
    </Page>
  )
}
