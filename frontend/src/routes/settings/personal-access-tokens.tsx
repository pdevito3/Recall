import { createFileRoute } from '@tanstack/react-router'
import { useRef } from 'react'

import { Page } from '@/components/page'
import { PageHeading } from '@/components/page-heading'
import { useMembership } from '@/domain/membership/apis/get-membership'
import { useMyPersonalAccessTokens } from '@/domain/personal-access-tokens/apis/personal-access-tokens'
import { NewTokenDialog } from '@/domain/personal-access-tokens/components/new-token-dialog'
import { TokenList } from '@/domain/personal-access-tokens/components/token-list'

export const Route = createFileRoute('/settings/personal-access-tokens')({
  component: PersonalAccessTokensPage,
  staticData: { breadcrumb: 'Personal access tokens', breadcrumbParent: { title: 'Settings', to: '/settings' } },
})

function PersonalAccessTokensPage() {
  const membership = useMembership()
  const tokens = useMyPersonalAccessTokens()
  const headingRef = useRef<HTMLHeadingElement>(null)

  return (
    <Page>
      <PageHeading ref={headingRef}>Personal access tokens</PageHeading>
      <div className="flex flex-wrap items-end justify-between gap-4">
        <p className="max-w-prose text-muted-foreground">
          A token lets a script or an MCP client call Recall as you. The client sends it as{' '}
          <code className="font-mono text-sm wrap-anywhere">Authorization: Bearer</code>. Revoke a token that you no longer use.
        </p>
        {membership.status === 'member' && <NewTokenDialog permissions={membership.user.permissions} />}
      </div>
      <TokenList
        tokens={tokens}
        label="Your personal access tokens"
        emptyText="You have no personal access tokens yet. Make one for each client that you use."
        headingRef={headingRef}
      />
    </Page>
  )
}
