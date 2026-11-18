import { useBlocker } from '@tanstack/react-router'
import { useRef, useState } from 'react'

import type { PersonalAccessTokenCreatedDto } from '@/api/generated/models/types/personal-access-token-created-dto'
import { Notification } from '@/components/notification'
import { Button } from '@/components/ui/button'
import {
  ResponsiveDialog,
  ResponsiveDialogClose,
  ResponsiveDialogDescription,
  ResponsiveDialogFooter,
  ResponsiveDialogHeader,
  ResponsiveDialogTitle,
  ResponsiveDialogTrigger,
} from '@/components/ui/responsive-dialog'
import { isDeparting } from '@/domain/auth/apis/leave-the-app'
import { copyToClipboard } from '@/lib/copy-to-clipboard'
import { browserTimeZone, formatInstant, todayAt } from '@/lib/zoned-time'

import { useAddPersonalAccessToken } from '../apis/personal-access-tokens'
import { TokenForm } from './token-form'

interface NewTokenDialogProps {
  permissions: readonly string[]
}

export function NewTokenDialog({ permissions }: NewTokenDialogProps) {
  const add = useAddPersonalAccessToken()
  const isLocked = add.isPending || add.data !== undefined
  // A Back while the question shows goes through. A block of it too would put back only one of the two steps at Stay.
  const isAsking = useRef(false)
  const leaving = useBlocker({
    shouldBlockFn: () => {
      if (isAsking.current) return false
      isAsking.current = true
      return true
    },
    disabled: !isLocked,
    enableBeforeUnload: () => !isDeparting(),
    withResolver: true,
  })

  return (
    <>
      <ResponsiveDialogTrigger onOpenChange={(isOpen) => { if (!isOpen) add.reset() }}>
        <Button>New token</Button>
        <ResponsiveDialog isDismissable={!isLocked} isKeyboardDismissDisabled={isLocked} showCloseButton={!isLocked}>
          <NewToken add={add} permissions={permissions} />
        </ResponsiveDialog>
      </ResponsiveDialogTrigger>
      <ResponsiveDialog
        role="alertdialog"
        isOpen={leaving.status === 'blocked'}
        onOpenChange={(isOpen) => {
          if (isOpen) return
          isAsking.current = false
          leaving.reset?.()
        }}
        isDismissable={false}
        showCloseButton={false}
      >
        <ResponsiveDialogHeader>
          <ResponsiveDialogTitle>Leave without your token?</ResponsiveDialogTitle>
          <ResponsiveDialogDescription>
            Recall cannot show this token again. Stay to copy it, or leave and make a new token when you need one.
          </ResponsiveDialogDescription>
        </ResponsiveDialogHeader>
        <ResponsiveDialogFooter>
          <Button variant="outline" onPress={() => leaving.proceed?.()}>
            Leave
          </Button>
          <ResponsiveDialogClose variant="default">Stay</ResponsiveDialogClose>
        </ResponsiveDialogFooter>
      </ResponsiveDialog>
    </>
  )
}

function NewToken({ add, permissions }: NewTokenDialogProps & { add: ReturnType<typeof useAddPersonalAccessToken> }) {
  const [zone] = useState(browserTimeZone)
  const [today] = useState(() => todayAt(zone, new Date()))
  // A second submit in the same task still reads isPending as false, so only a ref stops a second create.
  const isSending = useRef(false)

  if (add.data) return <CreatedToken created={add.data} />

  return (
    <TokenForm
      permissions={permissions}
      today={today}
      zone={zone}
      isPending={add.isPending}
      onCreate={(body, onError) => {
        if (isSending.current) return
        isSending.current = true
        add.mutate(body, { onError, onSettled: () => (isSending.current = false) })
      }}
    />
  )
}

function CreatedToken({ created }: { created: PersonalAccessTokenCreatedDto }) {
  const { token, value } = created
  const expiry = token.expiresAt
    ? `It works until ${formatInstant(new Date(token.expiresAt), browserTimeZone(), { year: 'numeric', month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit' })}.`
    : 'It works until you revoke it.'

  const handleCopy = () =>
    copyToClipboard(value).then(
      () => Notification.success('Token copied'),
      () => Notification.error('The browser did not copy the token. Select the token, and copy it by hand.'),
    )

  return (
    <div className="flex flex-col gap-4">
      <ResponsiveDialogHeader>
        <ResponsiveDialogTitle>Copy your new token</ResponsiveDialogTitle>
        <ResponsiveDialogDescription>
          This is the only time that Recall shows the token {token.name}. Recall keeps only a hash of it, so copy it into
          your client before you close this dialog. {expiry}
        </ResponsiveDialogDescription>
      </ResponsiveDialogHeader>
      <p className="rounded-lg border bg-muted p-3 font-mono text-sm break-all select-all">
        {value}
      </p>
      <ResponsiveDialogFooter>
        <Button variant="outline" onPress={() => void handleCopy()}>
          Copy token
        </Button>
        <ResponsiveDialogClose variant="default">Done</ResponsiveDialogClose>
      </ResponsiveDialogFooter>
    </div>
  )
}
