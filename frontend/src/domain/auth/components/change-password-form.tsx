import { zodResolver } from '@hookform/resolvers/zod'
import { useRef } from 'react'
import { useForm } from 'react-hook-form'
import * as z from 'zod'

import { Notification } from '@/components/notification'
import { RefusedSaveAlert } from '@/components/refused-save-alert'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { useServerErrors } from '@/hooks/use-server-errors'
import { focusFirstError } from '@/lib/focus-first-error'

import { useChangePassword } from '../apis/change-password'

// The Server checks the same limits and the list of leaked passwords (PWD-8, PWD-9). These checks only save a round trip.
const minLength = 15
const maxLength = 256
const FIELDS = ['currentPassword', 'newPassword'] as const

const lengthOf = (text: string) => [...text.normalize('NFKC')].length

const formSchema = z
  .object({
    currentPassword: z.string().min(1, 'Type your current password.'),
    newPassword: z
      .string()
      .refine((text) => lengthOf(text) >= minLength, 'Use at least 15 characters. A phrase of four words is long enough and easy to remember.')
      .refine((text) => lengthOf(text) <= maxLength, 'Use no more than 256 characters.'),
    repeatPassword: z.string(),
  })
  .refine((values) => values.repeatPassword === values.newPassword, { path: ['repeatPassword'], message: 'The two new passwords differ. Type the same password twice.' })

type FormValues = z.infer<typeof formSchema>

const emptyForm: FormValues = { currentPassword: '', newPassword: '', repeatPassword: '' }

const fieldError = (id: string, message: string | undefined) =>
  message && (
    <p id={id} className="text-sm text-destructive wrap-anywhere">
      {message}
    </p>
  )

export function ChangePasswordForm({ loginName }: { loginName: string }) {
  const change = useChangePassword()
  const form = useForm<FormValues>({ resolver: zodResolver(formSchema), defaultValues: emptyForm, shouldFocusError: false })
  const serverErrors = useServerErrors(form, FIELDS)
  // A second submit in the same task still reads isPending as false, so only a ref stops a second change.
  const isSending = useRef(false)
  const { errors } = form.formState

  const handleSubmit = form.handleSubmit(({ currentPassword, newPassword }) => {
    if (isSending.current) return
    isSending.current = true
    serverErrors.clear()
    change.mutate(
      { currentPassword, newPassword },
      {
        onSuccess: () => {
          form.reset(emptyForm)
          Notification.success('Password changed. Each other session and personal access token ended.')
        },
        onError: serverErrors.show,
        onSettled: () => (isSending.current = false),
      },
    )
  }, focusFirstError(form, [...FIELDS, 'repeatPassword']))

  const describedBy = (field: keyof FormValues, hint?: string) =>
    [hint, errors[field] ? `change-password-${field}-error` : undefined].filter(Boolean).join(' ') || undefined

  return (
    <form onSubmit={(event) => void handleSubmit(event)} noValidate className="flex max-w-md flex-col gap-4">
      {/* A password manager reads the login name of this field to save the new password for the right account. */}
      <input type="text" name="username" autoComplete="username" value={loginName} readOnly hidden />
      <p className="text-sm text-muted-foreground">
        This ends each of your other sessions and personal access tokens. This session stays signed in.
      </p>

      <div className="flex flex-col gap-2">
        <Label htmlFor="change-password-currentPassword">Current password</Label>
        <Input
          id="change-password-currentPassword"
          type="password"
          autoComplete="current-password"
          aria-invalid={Boolean(errors.currentPassword)}
          aria-describedby={describedBy('currentPassword')}
          {...form.register('currentPassword')}
        />
        {fieldError('change-password-currentPassword-error', errors.currentPassword?.message)}
      </div>

      <div className="flex flex-col gap-2">
        <Label htmlFor="change-password-newPassword">New password</Label>
        <Input
          id="change-password-newPassword"
          type="password"
          autoComplete="new-password"
          aria-invalid={Boolean(errors.newPassword)}
          aria-describedby={describedBy('newPassword', errors.newPassword ? undefined : 'change-password-newPassword-hint')}
          {...form.register('newPassword')}
        />
        {/* The error of a short password is this hint, so the field shows one or the other. */}
        {errors.newPassword ? (
          fieldError('change-password-newPassword-error', errors.newPassword.message)
        ) : (
          <p id="change-password-newPassword-hint" className="text-sm text-muted-foreground">
            Use at least 15 characters. A phrase of four words is long enough and easy to remember.
          </p>
        )}
      </div>

      <div className="flex flex-col gap-2">
        <Label htmlFor="change-password-repeatPassword">New password again</Label>
        <Input
          id="change-password-repeatPassword"
          type="password"
          autoComplete="new-password"
          aria-invalid={Boolean(errors.repeatPassword)}
          aria-describedby={describedBy('repeatPassword')}
          {...form.register('repeatPassword')}
        />
        {fieldError('change-password-repeatPassword-error', errors.repeatPassword?.message)}
      </div>

      <RefusedSaveAlert problem={serverErrors.problem} />

      <Button type="submit" className="self-start" isPending={change.isPending}>
        Change password
      </Button>
    </form>
  )
}
