import { Button } from '@/components/ui/button'
import { PageHeading } from '@/components/page-heading'

export function PageError() {
  return (
    <div className="mx-auto flex max-w-md flex-col items-center px-4 py-16 text-center">
      <PageHeading>This page did not load</PageHeading>
      <p className="mt-2 text-muted-foreground">Check your connection, then load the page again.</p>
      <Button className="mt-6" onPress={() => window.location.reload()}>
        Load the page again
      </Button>
    </div>
  )
}
