import { useId, useState, type ReactNode } from 'react'

import type { EnvironmentLoginProviderDto } from '@/api/generated/models/types/environment-login-provider-dto'
import type { LoginProviderDto, LoginProviderDtoStateEnumKey } from '@/api/generated/models/types/login-provider-dto'
import { Notification } from '@/components/notification'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardAction, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import {
  ResponsiveDialog,
  ResponsiveDialogClose,
  ResponsiveDialogDescription,
  ResponsiveDialogFooter,
  ResponsiveDialogHeader,
  ResponsiveDialogTitle,
  ResponsiveDialogTrigger,
} from '@/components/ui/responsive-dialog'
import { isProblemDetailsError } from '@/lib/problem-details'

import { useDeleteLoginProvider } from '../apis/login-providers'
import { LoginProviderForm } from './login-provider-form'

/** What the sign-in page does with a row in each state (IDP-3, IDP-5). */
const STATE_TEXT: Record<LoginProviderDtoStateEnumKey, { badge: string | null; text: string; isProblem: boolean }> = {
  Usable: { badge: null, text: 'On the sign-in page.', isProblem: false },
  SecretUnreadable: {
    badge: 'Not on the sign-in page',
    text: 'The server cannot read this client secret. Enter it again.',
    isProblem: true,
  },
  ReplacedByEnvironment: {
    badge: 'Replaced by the environment',
    text: 'A provider from the environment of the server has the same ID or issuer, so the sign-in page shows that one in its place.',
    isProblem: false,
  },
}

function Details({ children }: { children: ReactNode }) {
  return <dl className="grid gap-x-4 gap-y-1 text-sm sm:grid-cols-[max-content_1fr]">{children}</dl>
}

function Detail({ term, children }: { term: string; children: ReactNode }) {
  return (
    <>
      <dt className="text-muted-foreground">{term}</dt>
      <dd className="wrap-anywhere max-sm:mb-2">{children}</dd>
    </>
  )
}

export function EnvironmentProviderCard({ provider }: { provider: EnvironmentLoginProviderDto }) {
  const headingId = useId()
  return (
    <Card role="article" aria-labelledby={headingId}>
      <CardHeader>
        <CardTitle className="flex min-w-0 flex-wrap items-center gap-2 text-lg">
          <h3 id={headingId} className="wrap-anywhere">{provider.displayName}</h3>
          <Badge variant="outline">From the environment</Badge>
        </CardTitle>
      </CardHeader>
      <CardContent className="flex flex-col gap-4">
        <Details>
          <Detail term="ID">{provider.id}</Detail>
          <Detail term="Issuer">{provider.issuer}</Detail>
        </Details>
        <p className="text-sm text-muted-foreground">
          The server reads this provider from its environment when it starts, so this page cannot change it. Change it
          there, then restart the server.
        </p>
      </CardContent>
    </Card>
  )
}

interface LoginProviderCardProps {
  provider: LoginProviderDto
  /** The card goes away after a delete, so the page moves the focus. */
  onDeleted: () => void
}

export function LoginProviderCard({ provider, onDeleted }: LoginProviderCardProps) {
  const remove = useDeleteLoginProvider(provider.id)
  const headingId = useId()
  const [isEditing, setIsEditing] = useState(false)
  const state = STATE_TEXT[provider.state]
  const deleteRefusal = remove.error && (isProblemDetailsError(remove.error) ? remove.error.displayMessage : 'The delete failed. Try again.')

  const handleDelete = () =>
    remove.mutate(undefined, {
      onSuccess: () => {
        Notification.success('Login provider deleted')
        onDeleted()
      },
    })

  return (
    <Card role="article" aria-labelledby={headingId}>
      {/* With a large font on a phone, the name fills the row, and the buttons go under it, not past the edge. */}
      <CardHeader className="flex flex-wrap gap-2">
        <CardTitle className="flex min-w-0 flex-1 basis-40 flex-wrap items-center gap-2 text-lg">
          <h3 id={headingId} className="wrap-anywhere">{provider.displayName}</h3>
          {state.badge && <Badge variant={state.isProblem ? 'destructive' : 'outline'}>{state.badge}</Badge>}
        </CardTitle>
        <CardAction className="flex gap-2">
          <ResponsiveDialogTrigger isOpen={isEditing} onOpenChange={setIsEditing}>
            <Button variant="outline">Edit</Button>
            <ResponsiveDialog>
              <LoginProviderForm provider={provider} onSaved={() => setIsEditing(false)} />
            </ResponsiveDialog>
          </ResponsiveDialogTrigger>
          <ResponsiveDialogTrigger>
            <Button variant="outline" isPending={remove.isPending}>
              Delete
            </Button>
            <ResponsiveDialog>
              <ResponsiveDialogHeader>
                <ResponsiveDialogTitle>Delete {provider.displayName}?</ResponsiveDialogTitle>
                <ResponsiveDialogDescription>
                  The sign-in page stops showing it, and the server forgets its client secret. A person who signs in only
                  with this provider needs another way in.
                </ResponsiveDialogDescription>
              </ResponsiveDialogHeader>
              <ResponsiveDialogFooter>
                <ResponsiveDialogClose>Keep login provider</ResponsiveDialogClose>
                <ResponsiveDialogClose variant="destructive" onPress={handleDelete}>
                  Delete login provider
                </ResponsiveDialogClose>
              </ResponsiveDialogFooter>
            </ResponsiveDialog>
          </ResponsiveDialogTrigger>
        </CardAction>
      </CardHeader>
      <CardContent className="flex flex-col gap-4">
        <Details>
          <Detail term="ID">{provider.id}</Detail>
          <Detail term="Issuer">{provider.issuer}</Detail>
          <Detail term="Client ID">{provider.clientId}</Detail>
        </Details>
        <p className={state.isProblem ? 'text-sm text-destructive' : 'text-sm text-muted-foreground'}>{state.text}</p>
        {deleteRefusal && (
          <div role="alert" className="rounded-lg border border-destructive/20 bg-destructive/10 p-3 text-sm text-destructive wrap-anywhere">
            {deleteRefusal}
          </div>
        )}
      </CardContent>
    </Card>
  )
}
