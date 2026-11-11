import { Button } from '@/components/ui/button'
import { PageHeading } from '@/components/page-heading'
import { leaveTheApp, thisPageAgain, useLeftTheApp } from '@/domain/auth/apis/leave-the-app'
import { WordsLeftAtSignOut } from '@/domain/auth/components/words-left-at-sign-out'

export function PageError() {
  // The error page of the root route takes the place of the screen of the root, so it shows the words itself.
  const left = useLeftTheApp()
  if (left && left.why !== 'anotherPerson') return <WordsLeftAtSignOut {...left} />
  return (
    <div className="mx-auto flex max-w-md flex-col items-center px-4 py-16 text-center">
      <PageHeading>This page did not load</PageHeading>
      <p className="mt-2 text-muted-foreground">Check your connection, then load the page again.</p>
      <Button className="mt-6" onPress={() => leaveTheApp({ why: 'pageDidNotLoad', to: thisPageAgain() })}>
        Load the page again
      </Button>
    </div>
  )
}
