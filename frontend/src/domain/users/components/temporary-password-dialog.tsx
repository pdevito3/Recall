import { zodResolver } from '@hookform/resolvers/zod'
import { useRef } from 'react'
import { useForm } from 'react-hook-form'
import * as z from 'zod'

import type { TemporaryPasswordDto } from '@/api/generated/models/types/temporary-password-dto'
import type { UserDto } from '@/api/generated/models/types/user-dto'
import { temporaryPasswordForIssueDtoSchema } from '@/api/generated/models/zod/temporary-password-for-issue-dto'
import { Notification } from '@/components/notification'
import { RefusedSaveAlert } from '@/components/refused-save-alert'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import {
  ResponsiveDialog,
  ResponsiveDialogClose,
  ResponsiveDialogDescription,
  ResponsiveDialogFooter,
  ResponsiveDialogHeader,
  ResponsiveDialogTitle,
  ResponsiveDialogTrigger,
} from '@/components/ui/responsive-dialog'
import { useServerErrors } from '@/hooks/use-server-errors'
import { maxLengthOf } from '@/lib/api-contract'
import { copyToClipboard } from '@/lib/copy-to-clipboard'
import { focusFirstError } from '@/lib/focus-first-error'
import { browserTimeZone, formatInstant } from '@/lib/zoned-time'

import { useIssueTemporaryPassword } from '../apis/issue-temporary-password'

const loginNameMaxLength = maxLengthOf(temporaryPasswordForIssueDtoSchema.shape.loginName.unwrap().unwrap())
const FIELDS = ['loginName'] as const

const formSchema = (needsLoginName: boolean) =>
  z.object({
    loginName: needsLoginName
      ? z.string().trim().min(1, 'Give the person a login name. They type it to sign in.').max(loginNameMaxLength, `Use no more than ${loginNameMaxLength} characters.`)
      : z.string(),
  })

type FormValues = z.infer<ReturnType<typeof formSchema>>

interface TemporaryPasswordDialogProps {
  user: UserDto
  isSelf: boolean
}

export function TemporaryPasswordDialog({ user, isSelf }: TemporaryPasswordDialogProps) {
  const issue = useIssueTemporaryPassword(user.id)
  const isLocked = issue.isPending || issue.data !== undefined

  return (
    <ResponsiveDialogTrigger onOpenChange={(isOpen) => { if (!isOpen) issue.reset() }}>
      <Button variant="outline">Set temporary password</Button>
      <ResponsiveDialog isDismissable={!isLocked} isKeyboardDismissDisabled={isLocked} showCloseButton={!isLocked}>
        {issue.data ? <IssuedPassword user={user} issued={issue.data} isSelf={isSelf} /> : <AskForIssue user={user} isSelf={isSelf} issue={issue} />}
      </ResponsiveDialog>
    </ResponsiveDialogTrigger>
  )
}

function AskForIssue({ user, isSelf, issue }: TemporaryPasswordDialogProps & { issue: ReturnType<typeof useIssueTemporaryPassword> }) {
  const needsLoginName = !user.loginName
  const form = useForm<FormValues>({
    resolver: zodResolver(formSchema(needsLoginName)),
    defaultValues: { loginName: '' },
    shouldFocusError: false,
  })
  const serverErrors = useServerErrors(form, FIELDS)
  // A second submit in the same task still reads isPending as false, so only a ref stops a second issue.
  const isSending = useRef(false)
  const { errors } = form.formState

  const handleSubmit = form.handleSubmit(({ loginName }) => {
    if (isSending.current) return
    isSending.current = true
    serverErrors.clear()
    issue.mutate(needsLoginName ? loginName.trim() : undefined, {
      onError: serverErrors.show,
      onSettled: () => (isSending.current = false),
    })
  }, focusFirstError(form, FIELDS))

  const ends = isSelf
    ? 'This ends each of your other sessions and personal access tokens. You stay signed in here.'
    : `This ends each session and personal access token of ${user.fullName}, so they sign in again with the new password.`

  return (
    <form onSubmit={(event) => void handleSubmit(event)} noValidate className="flex flex-col gap-4">
      <ResponsiveDialogHeader>
        <ResponsiveDialogTitle>Set a temporary password for {user.fullName}?</ResponsiveDialogTitle>
        <ResponsiveDialogDescription>
          Recall makes a password that works for 7 days, and only to pick a new password. {ends}
        </ResponsiveDialogDescription>
      </ResponsiveDialogHeader>

      {needsLoginName ? (
        <div className="flex flex-col gap-2">
          <Label htmlFor="temporary-password-login-name">Login name</Label>
          <Input
            id="temporary-password-login-name"
            autoComplete="off"
            autoCapitalize="none"
            spellCheck={false}
            aria-invalid={Boolean(errors.loginName)}
            aria-describedby={errors.loginName ? 'temporary-password-login-name-hint temporary-password-login-name-error' : 'temporary-password-login-name-hint'}
            {...form.register('loginName')}
          />
          <p id="temporary-password-login-name-hint" className="text-sm text-muted-foreground">
            The name that {user.fullName} types to sign in. It has letters, digits, dots, dashes, underscores, or @ signs, and it cannot change later.
          </p>
          {errors.loginName?.message && (
            <p id="temporary-password-login-name-error" className="text-sm text-destructive wrap-anywhere">
              {errors.loginName.message}
            </p>
          )}
        </div>
      ) : (
        <p className="text-sm wrap-anywhere">
          Login name: <span className="font-mono">{user.loginName}</span>
        </p>
      )}

      <RefusedSaveAlert problem={serverErrors.problem} />

      <ResponsiveDialogFooter>
        <ResponsiveDialogClose isDisabled={issue.isPending}>Cancel</ResponsiveDialogClose>
        <Button type="submit" variant="destructive" isPending={issue.isPending}>
          Set temporary password
        </Button>
      </ResponsiveDialogFooter>
    </form>
  )
}

function IssuedPassword({ user, issued, isSelf }: { user: UserDto; issued: TemporaryPasswordDto; isSelf: boolean }) {
  const until = formatInstant(new Date(issued.expiresAt), browserTimeZone(), {
    year: 'numeric',
    month: 'short',
    day: 'numeric',
    hour: 'numeric',
    minute: '2-digit',
  })

  const handleCopy = () =>
    copyToClipboard(issued.password).then(
      () => Notification.success('Password copied'),
      () => Notification.error('The browser did not copy the password. Select the password, and copy it by hand.'),
    )

  return (
    <div className="flex flex-col gap-4">
      <ResponsiveDialogHeader>
        <ResponsiveDialogTitle>Give {isSelf ? 'yourself' : user.fullName} this password</ResponsiveDialogTitle>
        <ResponsiveDialogDescription>
          This is the only time that Recall shows it. It works for 7 days, until {until}, and only to pick a new password at the first sign-in.
        </ResponsiveDialogDescription>
      </ResponsiveDialogHeader>
      <dl className="grid grid-cols-[auto_1fr] gap-x-4 gap-y-2 text-sm">
        <dt className="text-muted-foreground">Login name</dt>
        <dd className="font-mono wrap-anywhere">{issued.loginName}</dd>
        <dt className="text-muted-foreground">Password</dt>
        <dd className="rounded-lg border bg-muted p-3 font-mono break-all select-all">{issued.password}</dd>
      </dl>
      <ResponsiveDialogFooter>
        <Button variant="outline" onPress={() => void handleCopy()}>
          Copy password
        </Button>
        <ResponsiveDialogClose variant="default">Done</ResponsiveDialogClose>
      </ResponsiveDialogFooter>
    </div>
  )
}
