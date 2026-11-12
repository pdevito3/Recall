import { zodResolver } from '@hookform/resolvers/zod'
import { useQueryClient } from '@tanstack/react-query'
import { Controller, useForm } from 'react-hook-form'

import type { CorrectionDto } from '@/api/generated/models/types/correction-dto'
import { Notification } from '@/components/notification'
import { RefusedSaveAlert } from '@/components/refused-save-alert'
import { LatestLoadedNote, StaleVersionAlert } from '@/components/stale-version-alert'
import { Button } from '@/components/ui/button'
import { Checkbox } from '@/components/ui/checkbox'
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

import { correctionOptions, useAddCorrection, useUpdateCorrection } from '../apis/corrections'
import {
  bodyOf,
  CORRECTION_FIELDS,
  correctionFormSchema,
  EMPTY_CORRECTION,
  valuesOf,
  type CorrectionFormValues,
} from './correction-form-schema'

const FIELD_LABELS: Record<(typeof CORRECTION_FIELDS)[number], string> = {
  canonicalTerm: 'Term',
  mishearings: 'Mishearings',
  hardReplace: 'Always replace',
}

function FieldMessage({ id, message }: { id: string; message?: string }) {
  if (!message) return null
  return (
    <p id={id} className="text-sm text-destructive wrap-anywhere">
      {message}
    </p>
  )
}

interface CorrectionFormProps {
  correction?: CorrectionDto
  onSaved: () => void
}

export function CorrectionForm({ correction, onSaved }: CorrectionFormProps) {
  const queryClient = useQueryClient()
  const add = useAddCorrection()
  const update = useUpdateCorrection()
  const form = useForm<CorrectionFormValues>({
    resolver: zodResolver(correctionFormSchema),
    defaultValues: correction ? valuesOf(correction) : EMPTY_CORRECTION,
    shouldFocusError: false,
  })
  const serverErrors = useServerErrors(form, CORRECTION_FIELDS)
  const latest = useLoadLatest(form, CORRECTION_FIELDS)
  const { errors } = form.formState
  const save = correction ? update : add

  const handleSaved = () => {
    Notification.success('Correction saved')
    onSaved()
  }

  const handleSubmit = form.handleSubmit((values) => {
    if (save.isPending) return
    serverErrors.clear()
    latest.clear()
    if (correction) {
      update.mutate(
        { id: correction.id, body: { ...bodyOf(values), version: values.version } },
        { onSuccess: handleSaved, onError: serverErrors.show },
      )
    } else {
      add.mutate(bodyOf(values), { onSuccess: handleSaved, onError: serverErrors.show })
    }
  }, focusFirstError(form, CORRECTION_FIELDS))

  const handleLoadLatest = async () => {
    if (!correction) return
    const loaded = await latest.load(async () => {
      const record = await queryClient
        .fetchQuery({ ...correctionOptions(correction.id), staleTime: 0 })
        .catch(() => undefined)
      return record && valuesOf(record)
    })
    if (loaded) serverErrors.clear()
  }

  return (
    <form onSubmit={(event) => void handleSubmit(event)} noValidate className="flex flex-col gap-4">
      <ResponsiveDialogHeader>
        <ResponsiveDialogTitle>{correction ? `Edit ${correction.canonicalTerm}` : 'Add a correction'}</ResponsiveDialogTitle>
        <ResponsiveDialogDescription>
          Cleanup fixes each mishearing of the term in the cleaned text. Your words as you said them do not change.
        </ResponsiveDialogDescription>
      </ResponsiveDialogHeader>

      <div className="flex flex-col gap-2">
        <Label htmlFor="correction-term">Term</Label>
        <Input
          id="correction-term"
          dir="auto"
          autoComplete="off"
          spellCheck={false}
          aria-invalid={Boolean(errors.canonicalTerm)}
          aria-describedby={errors.canonicalTerm ? 'correction-term-error' : undefined}
          {...form.register('canonicalTerm')}
        />
        <FieldMessage id="correction-term-error" message={errors.canonicalTerm?.message} />
      </div>

      <div className="flex flex-col gap-2">
        <Label htmlFor="correction-mishearings">Mishearings</Label>
        <Textarea
          id="correction-mishearings"
          dir="auto"
          spellCheck={false}
          aria-invalid={Boolean(errors.mishearings)}
          aria-describedby={errors.mishearings ? 'correction-mishearings-hint correction-mishearings-error' : 'correction-mishearings-hint'}
          {...form.register('mishearings')}
        />
        <p id="correction-mishearings-hint" className="text-sm text-muted-foreground">
          How dictation writes the term by mistake. Put each one on its own line.
        </p>
        <FieldMessage id="correction-mishearings-error" message={errors.mishearings?.message} />
      </div>

      <Controller
        control={form.control}
        name="hardReplace"
        render={({ field }) => (
          <div className="flex flex-col gap-1">
            <Checkbox
              className="max-md:min-h-11"
              isSelected={field.value}
              onChange={field.onChange}
              inputRef={field.ref}
              aria-describedby="correction-hard-replace-hint"
            >
              Always replace
            </Checkbox>
            <p id="correction-hard-replace-hint" className="text-sm text-muted-foreground">
              When this is on, Cleanup writes the term in place of each mishearing every time. When it is off, Cleanup
              gets the term as a hint.
            </p>
          </div>
        )}
      />

      <RefusedSaveAlert problem={serverErrors.problem} />
      <StaleVersionAlert
        noun="correction"
        problem={serverErrors.problem}
        isLoadingLatest={latest.isLoading}
        onLoadLatest={() => void handleLoadLatest()}
      />
      <LatestLoadedNote noun="correction" changed={latest.changed} labels={FIELD_LABELS} />

      <ResponsiveDialogFooter>
        <ResponsiveDialogClose>Cancel</ResponsiveDialogClose>
        <Button type="submit" isPending={save.isPending} isDisabled={latest.isLoading}>
          Save correction
        </Button>
      </ResponsiveDialogFooter>
    </form>
  )
}
