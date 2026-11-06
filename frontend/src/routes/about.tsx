import { createFileRoute } from '@tanstack/react-router'

import { Page } from '@/components/page'
import { PageHeading } from '@/components/page-heading'

export const Route = createFileRoute('/about')({
  component: About,
  staticData: { breadcrumb: 'About' },
})

function About() {
  return (
    <Page>
      <PageHeading>About</PageHeading>
      <div className="flex max-w-prose flex-col gap-4 leading-relaxed text-muted-foreground">
        <p>Recall is your private journal. Open it and write. Each entry belongs to the day and the time you started it.</p>
        <p>Three promises hold for each entry:</p>
        <ul className="flex list-disc flex-col gap-2 pl-5">
          <li>Your words stay as you wrote them. A cleaned version is a separate copy, and AI never changes your words.</li>
          <li>Each save keeps a version in the history of the entry, so you can read or restore an earlier version.</li>
          <li>The journal runs on a server that you control, and the data stays there.</li>
        </ul>
      </div>
    </Page>
  )
}
