import { zodResolver } from '@hookform/resolvers/zod'
import { Controller, useForm, useWatch } from 'react-hook-form'

import type { ModelProviderForCreationDto } from '@/api/generated/models/types/model-provider-for-creation-dto'
import { modelProviderForCreationDtoSchema } from '@/api/generated/models/zod/model-provider-for-creation-dto'
import { RefusedSaveAlert } from '@/components/refused-save-alert'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { useServerErrors } from '@/hooks/use-server-errors'

import { useAddModelProvider } from '../apis/model-providers'
import { KIND_LABELS, MODEL_PROVIDER_KINDS, isModelProviderKind } from './model-provider-kind'

const FIELDS = ['kind', 'name', 'baseUrl', 'apiKey'] as const

const EMPTY: ModelProviderForCreationDto = { kind: 'Anthropic', name: '', baseUrl: '', apiKey: '' }

function FieldMessage({ id, message }: { id: string; message?: string }) {
  if (!message) return null
  return (
    <p id={id} className="text-sm text-destructive">
      {message}
    </p>
  )
}

export function ConnectProviderForm() {
  const add = useAddModelProvider()
  const form = useForm<ModelProviderForCreationDto>({
    resolver: zodResolver(modelProviderForCreationDtoSchema),
    defaultValues: EMPTY,
  })
  const serverErrors = useServerErrors(form, FIELDS)
  const kind = useWatch({ control: form.control, name: 'kind' })
  const { errors } = form.formState

  const handleSubmit = form.handleSubmit((values) => {
    serverErrors.clear()
    add.mutate(values, {
      onSuccess: () => form.reset({ ...EMPTY, kind: values.kind }),
      onError: serverErrors.show,
    })
  })

  return (
    <form onSubmit={(event) => void handleSubmit(event)} noValidate className="flex flex-col gap-4">
      <Controller
        control={form.control}
        name="kind"
        render={({ field }) => (
          <Select
            value={field.value}
            onChange={(key) => {
              if (isModelProviderKind(key)) field.onChange(key)
            }}
            className="flex w-full flex-col gap-2"
          >
            <Label>Kind</Label>
            <SelectTrigger ref={field.ref} className="w-full">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              {MODEL_PROVIDER_KINDS.map((value) => (
                <SelectItem key={value} id={value}>
                  {KIND_LABELS[value]}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        )}
      />

      <div className="flex flex-col gap-2">
        <Label htmlFor="provider-name">Name</Label>
        <Input
          id="provider-name"
          aria-invalid={Boolean(errors.name)}
          aria-describedby={errors.name ? 'provider-name-error' : undefined}
          {...form.register('name')}
        />
        <FieldMessage id="provider-name-error" message={errors.name?.message} />
      </div>

      <div className="flex flex-col gap-2">
        <Label htmlFor="provider-base-url">Base URL</Label>
        <Input
          id="provider-base-url"
          type="url"
          aria-invalid={Boolean(errors.baseUrl)}
          aria-describedby="provider-base-url-hint"
          placeholder={kind === 'OpenAiCompatible' ? 'http://localhost:11434/v1' : 'https://api.anthropic.com'}
          {...form.register('baseUrl')}
        />
        <p id="provider-base-url-hint" className="text-sm text-muted-foreground">
          {kind === 'OpenAiCompatible'
            ? 'The address that comes before /models.'
            : 'Leave this blank to use the address of Anthropic.'}
        </p>
        <FieldMessage id="provider-base-url-error" message={errors.baseUrl?.message} />
      </div>

      <div className="flex flex-col gap-2">
        <Label htmlFor="provider-api-key">API key</Label>
        <Input
          id="provider-api-key"
          type="password"
          autoComplete="off"
          aria-invalid={Boolean(errors.apiKey)}
          aria-describedby="provider-api-key-hint"
          {...form.register('apiKey')}
        />
        <p id="provider-api-key-hint" className="text-sm text-muted-foreground">
          {kind === 'OpenAiCompatible'
            ? 'A local endpoint can work with no key. The server encrypts each key, and no page shows it again.'
            : 'The server encrypts the key, and no page shows it again.'}
        </p>
        <FieldMessage id="provider-api-key-error" message={errors.apiKey?.message} />
      </div>

      <RefusedSaveAlert problem={serverErrors.problem} />

      <div>
        <Button type="submit" isPending={add.isPending}>
          Connect
        </Button>
      </div>
    </form>
  )
}
