import { createFileRoute } from '@tanstack/react-router'
import * as z from 'zod'

import { useBreadcrumbName } from '@/components/breadcrumb-name'
import { Page } from '@/components/page'
import { QueryError } from '@/components/query-error'
import { useImportBatches } from '@/domain/imports/apis/imports'
import { ImportBatchMissing } from '@/domain/imports/components/import-batch-missing'
import { ImportResult } from '@/domain/imports/components/import-result'
import { ImportReview } from '@/domain/imports/components/import-review'

const searchSchema = z.object({
  page: z.number().int().min(1).optional().catch(undefined),
})

export const Route = createFileRoute('/imports/$id')({
  validateSearch: searchSchema,
  component: ImportBatchRoute,
  staticData: { breadcrumb: 'Batch', breadcrumbParent: { title: 'Imports', to: '/imports' } },
})

function ImportBatchRoute() {
  const { id } = Route.useParams()
  const { page } = Route.useSearch()
  const batches = useImportBatches()
  const batch = batches.data?.find((b) => b.id === id)
  useBreadcrumbName(batches.data && !batch ? 'Batch not found' : batch?.name)

  return (
    <Page>
      {batches.isPending && <p role="status" aria-label="Loading this batch" className="text-muted-foreground">Loading this batch</p>}
      {batches.isError && !batches.data && (
        <QueryError title="This batch did not load." error={batches.error} onRetry={() => void batches.refetch()} />
      )}
      {batches.data && !batch && <ImportBatchMissing />}
      {batch?.state === 'Accepted' && <ImportResult batch={batch} />}
      {batch?.state === 'Open' && <ImportReview key={batch.id} batch={batch} pageNumber={page ?? 1} />}
    </Page>
  )
}
