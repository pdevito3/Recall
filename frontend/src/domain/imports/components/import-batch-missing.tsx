import { PageHeading } from '@/components/page-heading'
import { Link } from '@/components/ui/link'

export function ImportBatchMissing() {
  return (
    <>
      <PageHeading>Batch not found</PageHeading>
      <p className="max-w-prose text-muted-foreground">This import batch does not exist. Pick a batch from your imports.</p>
      <Link to="/imports" className="flex w-fit items-center underline underline-offset-4 hover:text-primary max-sm:min-h-11">
        Go to your imports
      </Link>
    </>
  )
}
