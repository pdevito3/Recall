import { createFileRoute } from '@tanstack/react-router'

import { Page } from '@/components/page'
import { PageHeading } from '@/components/page-heading'
import { PeopleList } from '@/domain/people/components/people-list'

export const Route = createFileRoute('/people/')({
  component: PeopleRoute,
  staticData: { breadcrumb: 'People' },
})

function PeopleRoute() {
  return (
    <Page>
      <PageHeading>People</PageHeading>
      <PeopleList />
    </Page>
  )
}
