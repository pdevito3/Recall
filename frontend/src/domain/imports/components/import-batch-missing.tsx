import { PageHeading } from '@/components/page-heading'
import { Link } from '@/components/ui/link'

export function ImportBatchMissing() {
  return (
    <>
      <PageHeading>Batch not found</PageHeading>
      <p className="max-w-prose text-muted-foreground">
        This import batch does not exist. It may have been discarded.{' '}
        <Link to="/imports" className="underline underline-offset-4 hover:text-primary max-md:inline-flex max-md:min-h-11 max-md:items-center">Go to your imports</Link>
      </p>
    </>
  )
}
