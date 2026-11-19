import { createFileRoute } from '@tanstack/react-router'
import { useRef } from 'react'
import * as z from 'zod'

import { Page } from '@/components/page'
import { PageHeading } from '@/components/page-heading'
import { termMaxLength } from '@/domain/corrections/components/correction-form-schema'
import { CorrectionsList } from '@/domain/corrections/components/corrections-list'
import { KnownWords } from '@/domain/corrections/components/known-words'

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
  const handleSearch = (text: string, searchWhenTyped: string) =>
    navigate({ search: (prev) => ((prev.search ?? '') === searchWhenTyped ? { search: text || undefined } : prev), replace: true })

  return (
    <Page>
      <PageHeading ref={headingRef}>Corrections</PageHeading>
      <KnownWords />
      <CorrectionsList search={search} onSearch={handleSearch} headingRef={headingRef} />
    </Page>
  )
}
