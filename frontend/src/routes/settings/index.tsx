import { useQuery } from '@tanstack/react-query'
import { createFileRoute } from '@tanstack/react-router'

import { Page } from '@/components/page'
import { PageHeading } from '@/components/page-heading'
import { Link } from '@/components/ui/link'
import { authUserQuery } from '@/domain/auth/apis/get-user'
import { ChangePasswordForm } from '@/domain/auth/components/change-password-form'
import { SettingsForm } from '@/domain/settings/components/settings-form'

export const Route = createFileRoute('/settings/')({
  component: SettingsRoute,
  staticData: { breadcrumb: 'Settings' },
})

function SettingsRoute() {
  const loginName = useQuery(authUserQuery).data?.login_name
  return (
    <Page>
      <PageHeading>Settings</PageHeading>
      <SettingsForm />
      <section className="flex flex-col gap-2" aria-labelledby="settings-tokens">
        <h2 id="settings-tokens" className="text-lg font-semibold">Personal access tokens</h2>
        <p className="text-sm text-muted-foreground">A token lets a script or an MCP client call Recall as you.</p>
        <Link to="/settings/personal-access-tokens" className="self-start text-sm underline underline-offset-4 max-md:min-h-11 max-md:content-center">
          Manage personal access tokens
        </Link>
      </section>
      {loginName && (
        <section className="flex flex-col gap-2" aria-labelledby="settings-password">
          <h2 id="settings-password" className="text-lg font-semibold">Password</h2>
          <ChangePasswordForm loginName={loginName} />
        </section>
      )}
    </Page>
  )
}
