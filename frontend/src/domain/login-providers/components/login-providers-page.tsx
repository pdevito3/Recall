import { Fragment, useRef, useState } from 'react'

import { Notification } from '@/components/notification'
import { Page } from '@/components/page'
import { PageHeading } from '@/components/page-heading'
import { QueryError } from '@/components/query-error'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { ResponsiveDialog, ResponsiveDialogTrigger } from '@/components/ui/responsive-dialog'
import { useCanManageUserAccess } from '@/domain/membership/apis/get-membership'
import { copyToClipboard } from '@/lib/copy-to-clipboard'

import { useLoginProviders } from '../apis/login-providers'
import { useLoginSettings } from '../apis/login-settings'
import { EnvironmentProviderCard, LoginProviderCard } from './login-provider-card'
import { LoginProviderForm } from './login-provider-form'
import { PasswordLoginCard } from './password-login-card'

function CallbackUrl({ url }: { url: string }) {
  const handleCopy = () =>
    copyToClipboard(url).then(
      () => Notification.success('Callback URL copied'),
      () => Notification.error('The browser did not copy the callback URL. Select it, and copy it by hand.'),
    )

  return (
    <Card>
      <CardHeader>
        <CardTitle className="text-lg">
          <h2>Callback URL</h2>
        </CardTitle>
      </CardHeader>
      <CardContent className="flex flex-col gap-3">
        <p className="text-sm text-muted-foreground">
          Register this URL at the provider as a redirect URI of the client that you give Recall.
        </p>
        {/* A line breaks after a slash first, so a part of the URL such as callback stays whole. */}
        <p className="rounded-lg border bg-muted p-3 font-mono text-sm wrap-anywhere select-all">
          {url.split(/(?<=\/)(?!\/)/).map((part, index) => (
            <Fragment key={index}>
              {index > 0 && <wbr />}
              {part}
            </Fragment>
          ))}
        </p>
        <Button variant="outline" className="self-start" onPress={() => void handleCopy()}>
          Copy callback URL
        </Button>
      </CardContent>
    </Card>
  )
}

function AddProvider() {
  const [isOpen, setIsOpen] = useState(false)
  return (
    <ResponsiveDialogTrigger isOpen={isOpen} onOpenChange={setIsOpen}>
      <Button>Add a login provider</Button>
      <ResponsiveDialog>
        <LoginProviderForm onSaved={() => setIsOpen(false)} />
      </ResponsiveDialog>
    </ResponsiveDialogTrigger>
  )
}

function Providers() {
  const providers = useLoginProviders(true)
  const passwordsOff = useLoginSettings().data?.passwordLogin === 'Off'
  const listHeadingRef = useRef<HTMLHeadingElement>(null)

  if (providers.isPending) {
    return (
      <p role="status" aria-label="Loading login providers">
        Loading login providers
      </p>
    )
  }
  // A failed read again keeps the last list, so an open form, such as Load the latest of an edit, stays on the screen.
  if (!providers.data) {
    return <QueryError title="The login providers did not load." error={providers.error} onRetry={() => void providers.refetch()} />
  }

  const { items, environmentProviders, callbackUrl } = providers.data
  return (
    <>
      <CallbackUrl url={callbackUrl} />
      {/* In the order of the sign-in page: each provider from the environment, then each one added here (IDP-1). */}
      <section aria-labelledby="login-providers-heading" className="flex flex-col gap-4">
        <div className="flex flex-wrap items-center justify-between gap-2">
          <h2 id="login-providers-heading" ref={listHeadingRef} tabIndex={-1} className="text-lg font-semibold outline-none">
            Providers
          </h2>
          <AddProvider />
        </div>
        {items.length === 0 && environmentProviders.length === 0 && (
          <p className="text-sm text-muted-foreground">
            {passwordsOff
              ? 'No login provider is set up, and password login is off, so only the claim-owner file of the server lets anyone in. Add a provider, or turn password login on.'
              : 'No login provider is set up, so people sign in with a password.'}
          </p>
        )}
        {environmentProviders.map((provider) => (
          <EnvironmentProviderCard key={provider.id} provider={provider} />
        ))}
        {items.map((provider) => (
          <LoginProviderCard key={provider.id} provider={provider} onDeleted={() => listHeadingRef.current?.focus()} />
        ))}
      </section>
    </>
  )
}

export function LoginProvidersPage() {
  const canManage = useCanManageUserAccess()

  return (
    <Page>
      <PageHeading>Login providers</PageHeading>
      {canManage ? (
        <div className="flex flex-col gap-6">
          <Providers />
          <PasswordLoginCard />
        </div>
      ) : (
        canManage === false && (
          <p className="text-sm text-muted-foreground">An Admin sets up the login providers of this install.</p>
        )
      )}
    </Page>
  )
}
