import { createFileRoute } from '@tanstack/react-router'

import { Page } from '@/components/page'
import { PageHeading } from '@/components/page-heading'
import { SettingsForm } from '@/domain/settings/components/settings-form'

export const Route = createFileRoute('/settings/')({
  component: SettingsRoute,
  staticData: { breadcrumb: 'Settings' },
})

function SettingsRoute() {
  return (
    <Page>
      <PageHeading>Settings</PageHeading>
      <SettingsForm />
    </Page>
  )
}
