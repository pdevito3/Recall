import { useState } from 'react'

import type { PersonalAccessTokenCreatedDto } from '@/api/generated/models/types/personal-access-token-created-dto'
import { LeaveQuestion } from '@/components/leave-question'
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
import { useShownOnce } from '@/hooks/use-shown-once'
import { copyToClipboard } from '@/lib/copy-to-clipboard'
import { browserTimeZone, formatInstant, todayAt } from '@/lib/zoned-time'

import { useAddPersonalAccessToken } from '../apis/personal-access-tokens'
import { TokenForm, type TokenFormProps } from './token-form'

interface NewTokenDialogProps {
  permissions: readonly string[]
  canGiveJobTools: boolean
}

export function NewTokenDialog({ permissions, canGiveJobTools }: NewTokenDialogProps) {
  const add = useAddPersonalAccessToken()
  const { isOpen, onOpenChange, send, isLocked, leaving } = useShownOnce(add)

  return (
    <>
      <ResponsiveDialogTrigger isOpen={isOpen} onOpenChange={onOpenChange}>
        <Button>New token</Button>
        <ResponsiveDialog isDismissable={!isLocked} isKeyboardDismissDisabled={isLocked} showCloseButton={!isLocked}>
          <NewToken add={add} permissions={permissions} canGiveJobTools={canGiveJobTools} onCreate={send} />
        </ResponsiveDialog>
      </ResponsiveDialogTrigger>
      <LeaveQuestion
        leaving={leaving}
        title="Leave without your token?"
        description="Recall cannot show this token again. Stay to copy it, or leave and make a new token when you need one."
      />
    </>
  )
}

function NewToken({
  add,
  permissions,
  canGiveJobTools,
  onCreate,
}: NewTokenDialogProps & Pick<TokenFormProps, 'onCreate'> & { add: ReturnType<typeof useAddPersonalAccessToken> }) {
  const [zone] = useState(browserTimeZone)
  const [today] = useState(() => todayAt(zone, new Date()))

  if (add.data) return <CreatedToken created={add.data} />

  return (
    <TokenForm
      permissions={permissions}
      canGiveJobTools={canGiveJobTools}
      today={today}
      zone={zone}
      isPending={add.isPending}
      onCreate={onCreate}
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
