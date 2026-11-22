import { zodResolver } from '@hookform/resolvers/zod'
import type { CalendarDate } from '@internationalized/date'
import { Controller, useForm } from 'react-hook-form'

import type { PersonalAccessTokenForCreationDto } from '@/api/generated/models/types/personal-access-token-for-creation-dto'
import { RefusedSaveAlert } from '@/components/refused-save-alert'
import { Button } from '@/components/ui/button'
import { Checkbox, CheckboxGroupWithLabel } from '@/components/ui/checkbox'
import { JollyDatePicker } from '@/components/ui/date-picker'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import {
  ResponsiveDialogClose,
  ResponsiveDialogDescription,
  ResponsiveDialogFooter,
  ResponsiveDialogHeader,
  ResponsiveDialogTitle,
} from '@/components/ui/responsive-dialog'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { useServerErrors } from '@/hooks/use-server-errors'
import { focusFirstError } from '@/lib/focus-first-error'
import { formatDate, type TimeZone } from '@/lib/zoned-time'

import { inFormOrder, isTokenPermission, permissionLabel } from './permission-labels'
import {
  bodyOf,
  defaultExpiryDate,
  DEFAULT_EXPIRY_DAYS,
  EMPTY_TOKEN,
  EXPIRY_CHOICES,
  journalAccessAfter,
  TOKEN_FIELDS,
  tokenFormSchema,
  type ExpiryChoice,
  type TokenFormValues,
} from './token-form-schema'

const dayWords = (date: CalendarDate) => formatDate(date, { year: 'numeric', month: 'short', day: 'numeric' })

const isExpiryChoice = (value: unknown): value is ExpiryChoice => EXPIRY_CHOICES.some((choice) => choice === value)

function FieldMessage({ id, message }: { id: string; message?: string }) {
  if (!message) return null
  return (
    <p id={id} className="text-sm text-destructive wrap-anywhere">
      {message}
    </p>
  )
}

export interface TokenFormProps {
  permissions: readonly string[]
  today: CalendarDate
  zone: TimeZone
  isPending: boolean
  onCreate: (body: PersonalAccessTokenForCreationDto, onError: (error: unknown) => void) => void
}

export function TokenForm({ permissions, today, zone, isPending, onCreate }: TokenFormProps) {
  const form = useForm<TokenFormValues>({
    resolver: zodResolver(tokenFormSchema(today)),
    defaultValues: EMPTY_TOKEN,
    shouldFocusError: false,
  })
  const serverErrors = useServerErrors(form, TOKEN_FIELDS)
  const { errors } = form.formState
  const expiry = form.watch('expiry')
  const choices = inFormOrder(permissions)

  const expiryLabels: Record<ExpiryChoice, string> = {
    default: `In ${DEFAULT_EXPIRY_DAYS} days, on ${dayWords(defaultExpiryDate(today))}`,
    date: 'On a date that I pick',
    never: 'Never',
  }

  const handleSubmit = form.handleSubmit((values) => {
    serverErrors.clear()
    onCreate(bodyOf(values, today, zone), serverErrors.show)
  }, focusFirstError(form, TOKEN_FIELDS))

  const setJournalAccess = (field: 'readJournal' | 'writeJournal', isSelected: boolean) => {
    const access = journalAccessAfter(form.getValues(), field, isSelected)
    form.setValue('readJournal', access.readJournal)
    form.setValue('writeJournal', access.writeJournal, { shouldValidate: form.formState.isSubmitted })
  }

  return (
    <form onSubmit={(event) => void handleSubmit(event)} noValidate className="flex flex-col gap-4">
      <ResponsiveDialogHeader>
        <ResponsiveDialogTitle>New personal access token</ResponsiveDialogTitle>
        <ResponsiveDialogDescription>
          A script or an MCP client sends the token to act as you. It can do only what you pick here, and only while you can do it too.
        </ResponsiveDialogDescription>
      </ResponsiveDialogHeader>

      <div className="flex flex-col gap-2">
        <Label htmlFor="token-name">Name</Label>
        <Input
          id="token-name"
          dir="auto"
          autoComplete="off"
          aria-invalid={Boolean(errors.name)}
          aria-describedby={errors.name ? 'token-name-hint token-name-error' : 'token-name-hint'}
          {...form.register('name')}
        />
        <p id="token-name-hint" className="text-sm text-muted-foreground">A name that tells you where you use the token, such as Laptop.</p>
        <FieldMessage id="token-name-error" message={errors.name?.message} />
      </div>

      <fieldset className="flex flex-col gap-3">
        <legend className="mb-2 text-sm font-medium">Journal</legend>
        <Controller
          control={form.control}
          name="readJournal"
          render={({ field }) => (
            <Checkbox
              className="max-md:min-h-11"
              isSelected={field.value}
              onChange={(isSelected) => setJournalAccess('readJournal', isSelected)}
              inputRef={field.ref}
            >
              Read my journal
            </Checkbox>
          )}
        />
        <Controller
          control={form.control}
          name="writeJournal"
          render={({ field }) => (
            <Checkbox
              className="max-md:min-h-11"
              isSelected={field.value}
              onChange={(isSelected) => setJournalAccess('writeJournal', isSelected)}
              inputRef={field.ref}
              aria-describedby="token-journal-hint"
            >
              Change my journal
            </Checkbox>
          )}
        />
        <p id="token-journal-hint" className="text-sm text-muted-foreground">
          A token that changes your journal also reads it. With no access to the journal, the token also cannot read your settings.
        </p>
        <FieldMessage id="token-journal-error" message={errors.writeJournal?.message} />
      </fieldset>

      {choices.length > 0 && (
        <Controller
          control={form.control}
          name="permissions"
          render={({ field }) => (
            <CheckboxGroupWithLabel
              label="The token can also"
              value={field.value}
              onChange={(picked) => field.onChange(picked.filter(isTokenPermission))}
            >
              {choices.map((permission) => (
                <Checkbox key={permission} value={permission} className="max-md:min-h-11">
                  {permissionLabel(permission)}
                </Checkbox>
              ))}
            </CheckboxGroupWithLabel>
          )}
        />
      )}

      <Controller
        control={form.control}
        name="expiry"
        render={({ field }) => (
          <Select
            value={field.value}
            onChange={(key) => {
              if (isExpiryChoice(key)) field.onChange(key)
            }}
            className="flex w-full flex-col gap-2"
          >
            <Label>Expires</Label>
            <SelectTrigger ref={field.ref} className="w-full max-md:h-11">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              {EXPIRY_CHOICES.map((choice) => (
                <SelectItem key={choice} id={choice}>
                  {expiryLabels[choice]}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        )}
      />

      {expiry === 'date' && (
        <Controller
          control={form.control}
          name="expiresAt"
          render={({ field }) => (
            <JollyDatePicker<CalendarDate>
              ref={field.ref}
              label="Last day"
              description="The token works until the end of this day."
              value={field.value}
              onChange={(date) => field.onChange(date)}
              isInvalid={Boolean(errors.expiresAt)}
              errorMessage={errors.expiresAt?.message}
              timeZone={zone}
            />
          )}
        />
      )}
      {expiry !== 'date' && <FieldMessage id="token-expiry-error" message={errors.expiresAt?.message} />}

      <RefusedSaveAlert problem={serverErrors.problem} />

      <ResponsiveDialogFooter>
        <ResponsiveDialogClose isDisabled={isPending}>Cancel</ResponsiveDialogClose>
        <Button type="submit" isPending={isPending}>
          Create token
        </Button>
      </ResponsiveDialogFooter>
    </form>
  )
}
