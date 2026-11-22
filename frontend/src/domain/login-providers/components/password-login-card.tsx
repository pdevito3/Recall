import { useRef } from 'react'

import type { LoginSettingsDtoPasswordLoginEnumKey } from '@/api/generated/models/types/login-settings-dto'
import { Notification } from '@/components/notification'
import { QueryError } from '@/components/query-error'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import {
  ResponsiveDialog,
  ResponsiveDialogClose,
  ResponsiveDialogDescription,
  ResponsiveDialogFooter,
  ResponsiveDialogHeader,
  ResponsiveDialogTitle,
  ResponsiveDialogTrigger,
} from '@/components/ui/responsive-dialog'
import { isProblemDetailsError, isStaleVersionError } from '@/lib/problem-details'

import { useLoginSettings, useUpdateLoginSettings } from '../apis/login-settings'

/** What the sign-in page does in each state (IDP-13, IDP-15, IDP-20). */
const STATE_TEXT: Record<LoginSettingsDtoPasswordLoginEnumKey, { badge: string; text: string; done: string }> = {
  On: {
    badge: 'On',
    text: 'People can sign in with a password. Turn it off once each tenant has an Admin who signs in with a provider.',
    done: 'Password login turned on',
  },
  Off: {
    badge: 'Off',
    text: 'No one signs in with a password. A temporary password only links a provider. The claim-owner file of the server still works.',
    done: 'Password login turned off',
  },
}

export function PasswordLoginCard() {
  const settings = useLoginSettings()
  const turn = useUpdateLoginSettings()
  const headingRef = useRef<HTMLHeadingElement>(null)

  if (settings.isPending) {
    return (
      <p role="status" aria-label="Loading password login">
        Loading password login
      </p>
    )
  }
  // A failed read again after a refusal keeps the last setting, so the card says that the read failed, below.
  if (!settings.data) {
    return <QueryError title="The password login setting did not load." error={settings.error} onRetry={() => void settings.refetch()} />
  }

  const { passwordLogin, version } = settings.data
  const state = STATE_TEXT[passwordLogin]
  // The card reads the setting again after a stale turn, so it says that, not the reload of the Server text. The 409 of IDP-13 says what to do.
  // A read again that failed keeps the old setting and version, so the card says that in place of the stale text.
  const refusal =
    turn.error &&
    !(settings.isError && isStaleVersionError(turn.error)) &&
    (isStaleVersionError(turn.error)
      ? 'Password login changed after you loaded this page. The card shows the setting now.'
      : isProblemDetailsError(turn.error)
        ? turn.error.displayMessage
        : 'The change failed. Try again.')

  const handleTurn = (to: LoginSettingsDtoPasswordLoginEnumKey) =>
    turn.mutate(
      { passwordLogin: to, version },
      {
        onSuccess: () => Notification.success(STATE_TEXT[to].done),
        // The button of the other state takes the place of this one, so the focus goes to the heading.
        onSettled: () => headingRef.current?.focus(),
      },
    )

  return (
    <Card role="region" aria-labelledby="password-login-heading">
      <CardHeader>
        <CardTitle className="flex min-w-0 flex-wrap items-center gap-2 text-lg">
          <h2 id="password-login-heading" ref={headingRef} tabIndex={-1} className="outline-none">
            Password login
          </h2>
          <Badge variant={passwordLogin === 'On' ? 'secondary' : 'outline'}>{state.badge}</Badge>
        </CardTitle>
      </CardHeader>
      <CardContent className="flex flex-col gap-3">
        <p className="text-sm text-muted-foreground">{state.text}</p>
        {passwordLogin === 'On' ? (
          <ResponsiveDialogTrigger>
            <Button variant="outline" className="self-start" isPending={turn.isPending}>
              Turn password login off
            </Button>
            <ResponsiveDialog>
              <ResponsiveDialogHeader>
                <ResponsiveDialogTitle>Turn password login off?</ResponsiveDialogTitle>
                <ResponsiveDialogDescription>
                  No one signs in with a password, and no one changes a password. A temporary password that an Admin sets only
                  links a provider. The claim-owner file of the server still works. Sessions that a password started keep
                  working.
                </ResponsiveDialogDescription>
              </ResponsiveDialogHeader>
              <ResponsiveDialogFooter>
                <ResponsiveDialogClose>Keep password login on</ResponsiveDialogClose>
                <ResponsiveDialogClose variant="destructive" onPress={() => handleTurn('Off')}>
                  Turn password login off
                </ResponsiveDialogClose>
              </ResponsiveDialogFooter>
            </ResponsiveDialog>
          </ResponsiveDialogTrigger>
        ) : (
          <Button variant="outline" className="self-start" isPending={turn.isPending} onPress={() => handleTurn('On')}>
            Turn password login on
          </Button>
        )}
        {settings.isError && (
          <div role="alert" className="flex flex-col items-start gap-3 rounded-lg border border-destructive/20 bg-destructive/10 p-3 text-sm text-destructive wrap-anywhere">
            <p>The password login setting did not load, so this card can show an old setting. Try again before you turn it.</p>
            <Button variant="outline" onPress={() => void settings.refetch()}>
              Try again
            </Button>
          </div>
        )}
        {refusal && (
          <div role="alert" className="rounded-lg border border-destructive/20 bg-destructive/10 p-3 text-sm text-destructive wrap-anywhere">
            {refusal}
          </div>
        )}
      </CardContent>
    </Card>
  )
}
