import { zodResolver } from '@hookform/resolvers/zod'
import { useForm } from 'react-hook-form'
import * as z from 'zod'

import type { TemporaryPasswordDto } from '@/api/generated/models/types/temporary-password-dto'
import type { UserDto } from '@/api/generated/models/types/user-dto'
import { temporaryPasswordForIssueDtoSchema } from '@/api/generated/models/zod/temporary-password-for-issue-dto'
import { LeaveQuestion } from '@/components/leave-question'
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
import { useLoginSettings } from '@/domain/login-providers/apis/login-settings'
import { useServerErrors } from '@/hooks/use-server-errors'
import { useShownOnce } from '@/hooks/use-shown-once'
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
  // What the password does depends on password login (IDP-20), so the dialog reads it before it says anything, and again each time
  // it opens, because another Admin can turn it while this page is open. The caller holds manage_user_access, so it can read it.
  const settings = useLoginSettings()
  const passwordsOff = settings.data && settings.data.passwordLogin === 'Off'
  const { isOpen, onOpenChange, send, isLocked, leaving } = useShownOnce(issue)

  return (
    <>
      <ResponsiveDialogTrigger
        isOpen={isOpen}
        onOpenChange={(open) => {
          onOpenChange(open)
          if (open) void settings.refetch()
        }}
      >
        <Button variant="outline">Set temporary password</Button>
        <ResponsiveDialog isDismissable={!isLocked} isKeyboardDismissDisabled={isLocked} showCloseButton={!isLocked}>
          {issue.data ? (
            <IssuedPassword user={user} issued={issue.data} isSelf={isSelf} passwordsOff={passwordsOff ?? false} />
          ) : (
            <AskForIssue user={user} isSelf={isSelf} isPending={issue.isPending} settings={settings} onIssue={send} />
          )}
        </ResponsiveDialog>
      </ResponsiveDialogTrigger>
      <LeaveQuestion
        leaving={leaving}
        title="Leave without the password?"
        description="Recall cannot show this password again. Stay to copy it, or leave and set a new temporary password when you need one."
      />
    </>
  )
}

interface AskForIssueProps extends TemporaryPasswordDialogProps {
  isPending: boolean
  settings: ReturnType<typeof useLoginSettings>
  onIssue: (loginName: string | undefined, onError: (error: unknown) => void) => void
}

function AskForIssue({ user, isSelf, isPending, settings, onIssue }: AskForIssueProps) {
  const needsLoginName = !user.loginName
  const form = useForm<FormValues>({
    resolver: zodResolver(formSchema(needsLoginName)),
    defaultValues: { loginName: '' },
    shouldFocusError: false,
  })
  const serverErrors = useServerErrors(form, FIELDS)
  const { errors } = form.formState
  // While password login is Off, a temporary password only links a provider (IDP-20). Until the read of the open ends, the dialog says nothing of it.
  const passwordsOff = settings.isFetching || !settings.data ? undefined : settings.data.passwordLogin === 'Off'

  const handleSubmit = form.handleSubmit(({ loginName }) => {
    if (passwordsOff === undefined) return
    serverErrors.clear()
    onIssue(needsLoginName ? loginName.trim() : undefined, serverErrors.show)
  }, focusFirstError(form, FIELDS))

  const ends = isSelf
    ? 'This ends each of your other sessions and personal access tokens. You stay signed in here.'
    : `This ends each session and personal access token of ${user.fullName}, so they sign in again${passwordsOff ? '' : ' with the new password'}.`

  return (
    <form onSubmit={(event) => void handleSubmit(event)} noValidate className="flex flex-col gap-4">
      <ResponsiveDialogHeader>
        <ResponsiveDialogTitle>Set a temporary password for {user.fullName}?</ResponsiveDialogTitle>
        {passwordsOff === undefined ? (
          settings.isError && !settings.isFetching ? (
            <div role="alert" className="flex flex-wrap items-center gap-2 text-sm text-destructive">
              <p>The password login setting did not load, so Recall cannot say what this password does.</p>
              <Button variant="outline" size="sm" onPress={() => void settings.refetch()}>
                Try again
              </Button>
            </div>
          ) : (
            <p role="status" className="text-sm text-muted-foreground">
              Loading the password login setting
            </p>
          )
        ) : (
          <ResponsiveDialogDescription>
            {passwordsOff
              ? 'Password login is off, so Recall makes a password that works for 7 days, and only to link a provider at the first sign-in.'
              : 'Recall makes a password that works for 7 days, and only to pick a new password.'}{' '}
            {ends}
          </ResponsiveDialogDescription>
        )}
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
        <ResponsiveDialogClose isDisabled={isPending}>Cancel</ResponsiveDialogClose>
        <Button type="submit" variant="destructive" isPending={isPending} isDisabled={passwordsOff === undefined}>
          Set temporary password
        </Button>
      </ResponsiveDialogFooter>
    </form>
  )
}

function IssuedPassword({ user, issued, isSelf, passwordsOff }: { user: UserDto; issued: TemporaryPasswordDto; isSelf: boolean; passwordsOff: boolean }) {
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
          This is the only time that Recall shows it. It works for 7 days, until {until},{' '}
          {passwordsOff
            ? 'and only to link a provider at the first sign-in, because password login is off. After the link, the provider signs in.'
            : 'and only to pick a new password at the first sign-in.'}
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
