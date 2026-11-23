import { zodResolver } from '@hookform/resolvers/zod'
import { useQueryClient } from '@tanstack/react-query'
import { useForm } from 'react-hook-form'

import type { PersonDto } from '@/api/generated/models/types/person-dto'
import { Notification } from '@/components/notification'
import { RefusedSaveAlert } from '@/components/refused-save-alert'
import { LatestLoadedNote, StaleVersionAlert } from '@/components/stale-version-alert'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import {
  ResponsiveDialogClose,
  ResponsiveDialogDescription,
  ResponsiveDialogFooter,
  ResponsiveDialogHeader,
  ResponsiveDialogTitle,
} from '@/components/ui/responsive-dialog'
import { Textarea } from '@/components/ui/textarea'
import { useLoadLatest } from '@/hooks/use-load-latest'
import { useServerErrors } from '@/hooks/use-server-errors'
import { focusFirstError } from '@/lib/focus-first-error'

import { personOptions, useDescribePerson } from '../apis/people'
import {
  bodyOf,
  detailsOf,
  PERSON_DETAIL_FIELDS,
  personDetailsSchema,
  type PersonDetailsFormValues,
} from './person-details-form-schema'

const FIELD_LABELS: Record<(typeof PERSON_DETAIL_FIELDS)[number], string> = {
  fullName: 'Full name',
  notes: 'Notes',
}

function FieldMessage({ id, message }: { id: string; message?: string }) {
  if (!message) return null
  return (
    <p id={id} className="text-sm text-destructive wrap-anywhere">
      {message}
    </p>
  )
}

export function PersonDetailsForm({ person, onSaved }: { person: PersonDto; onSaved: () => void }) {
  const queryClient = useQueryClient()
  const describe = useDescribePerson()
  const form = useForm<PersonDetailsFormValues>({
    resolver: zodResolver(personDetailsSchema),
    defaultValues: detailsOf(person),
    shouldFocusError: false,
  })
  const serverErrors = useServerErrors(form, PERSON_DETAIL_FIELDS)
  const latest = useLoadLatest(form, PERSON_DETAIL_FIELDS)
  const { errors } = form.formState
  const fullNameId = `person-full-name-${person.id}`
  const notesId = `person-notes-${person.id}`

  const handleSubmit = form.handleSubmit((values) => {
    serverErrors.clear()
    latest.clear()
    describe.mutate(
      { id: person.id, body: bodyOf(values) },
      {
        onSuccess: () => {
          Notification.success('Details saved')
          onSaved()
        },
        onError: (error) => serverErrors.show(error),
      },
    )
  }, focusFirstError(form, PERSON_DETAIL_FIELDS))

  const handleLoadLatest = async () => {
    const loaded = await latest.load(async () => {
      const record = await queryClient.fetchQuery({ ...personOptions(person.id), staleTime: 0 }).catch(() => undefined)
      return record && detailsOf(record)
    })
    if (loaded) serverErrors.clear()
  }

  return (
    <form onSubmit={(event) => void handleSubmit(event)} noValidate className="flex flex-col gap-4">
      <ResponsiveDialogHeader>
        <ResponsiveDialogTitle>Details of {person.label}</ResponsiveDialogTitle>
        <ResponsiveDialogDescription>
          The details tell two people with the same name apart. Entries still show the name {person.label}.
        </ResponsiveDialogDescription>
      </ResponsiveDialogHeader>

      <div className="flex flex-col gap-2">
        <Label htmlFor={fullNameId}>Full name</Label>
        <Input
          id={fullNameId}
          dir="auto"
          autoComplete="off"
          aria-invalid={Boolean(errors.fullName)}
          aria-describedby={errors.fullName ? `${fullNameId}-error` : undefined}
          {...form.register('fullName')}
        />
        <FieldMessage id={`${fullNameId}-error`} message={errors.fullName?.message} />
      </div>

      <div className="flex flex-col gap-2">
        <Label htmlFor={notesId}>Notes</Label>
        <Textarea
          id={notesId}
          dir="auto"
          aria-invalid={Boolean(errors.notes)}
          aria-describedby={errors.notes ? `${notesId}-hint ${notesId}-error` : `${notesId}-hint`}
          {...form.register('notes')}
        />
        <p id={`${notesId}-hint`} className="text-sm text-muted-foreground">
          Such as how you know this person. Cleanup does not read the details.
        </p>
        <FieldMessage id={`${notesId}-error`} message={errors.notes?.message} />
      </div>

      <RefusedSaveAlert problem={serverErrors.problem} />
      <StaleVersionAlert
        noun="person"
        problem={serverErrors.problem}
        isLoadingLatest={latest.isLoading}
        onLoadLatest={() => void handleLoadLatest()}
      />
      <LatestLoadedNote noun="person" changed={latest.changed} labels={FIELD_LABELS} />

      <ResponsiveDialogFooter>
        <ResponsiveDialogClose>Cancel</ResponsiveDialogClose>
        <Button type="submit" isPending={describe.isPending} isDisabled={latest.isLoading}>
          Save details
        </Button>
      </ResponsiveDialogFooter>
    </form>
  )
}
