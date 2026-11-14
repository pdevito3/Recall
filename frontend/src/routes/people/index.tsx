import { createFileRoute } from '@tanstack/react-router'
import { useRef } from 'react'

import { Page } from '@/components/page'
import { PageHeading } from '@/components/page-heading'
import { PeopleList } from '@/domain/people/components/people-list'

export const Route = createFileRoute('/people/')({
  component: PeopleRoute,
  staticData: { breadcrumb: 'People' },
})

function PeopleRoute() {
  const headingRef = useRef<HTMLHeadingElement>(null)

  return (
    <Page>
      <PageHeading ref={headingRef}>People</PageHeading>
      <PeopleList headingRef={headingRef} />
    </Page>
  )
}
