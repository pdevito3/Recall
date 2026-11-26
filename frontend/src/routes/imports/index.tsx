import { createFileRoute } from '@tanstack/react-router'
import { useRef } from 'react'

import { Page } from '@/components/page'
import { PageHeading } from '@/components/page-heading'
import { useImportBatches } from '@/domain/imports/apis/imports'
import { ImportBatchList } from '@/domain/imports/components/import-batch-list'
import { ImportSkill } from '@/domain/imports/components/import-skill'

export const Route = createFileRoute('/imports/')({
  component: ImportsPage,
  staticData: { breadcrumb: 'Imports' },
})

function ImportsPage() {
  const batches = useImportBatches()
  const headingRef = useRef<HTMLHeadingElement>(null)

  return (
    <Page>
      <PageHeading ref={headingRef}>Imports</PageHeading>
      <p className="max-w-prose text-muted-foreground">
        An agent puts the entries of an old journal into an import batch. Review each batch here, then accept it to put its entries in your journal.
      </p>
      <ImportBatchList batches={batches} headingRef={headingRef} />
      <ImportSkill />
    </Page>
  )
}
