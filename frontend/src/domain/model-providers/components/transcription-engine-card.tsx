import { zodResolver } from '@hookform/resolvers/zod'
import { Controller, useForm } from 'react-hook-form'
import * as z from 'zod'

import type { ModelProviderDto, ModelProviderDtoKindEnumKey } from '@/api/generated/models/types/model-provider-dto'
import type { TranscriptionEngineDto } from '@/api/generated/models/types/transcription-engine-dto'
import { transcriptionEngineChoiceDtoSchema } from '@/api/generated/models/zod/transcription-engine-choice-dto'
import { QueryError } from '@/components/query-error'
import { RefusedSaveAlert } from '@/components/refused-save-alert'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { useServerErrors } from '@/hooks/use-server-errors'
import { stringWithMaxLengthOf } from '@/lib/api-contract'

import { useModelProviders } from '../apis/model-providers'
import { useChooseTranscriptionEngine, useTranscriptionEngine } from '../apis/transcription-engine'

const TAKES_AUDIO: Record<ModelProviderDtoKindEnumKey, boolean> = {
  Anthropic: false,
  OpenAiCompatible: true,
}

const schema = z.object({
  modelProviderId: z.string().min(1, 'Pick a model provider.'),
  model: stringWithMaxLengthOf(transcriptionEngineChoiceDtoSchema.shape.model.unwrap().unwrap()).min(
    1,
    'Enter the model that turns audio into text.',
  ),
})

type EngineChoice = z.infer<typeof schema>

const FIELDS = ['modelProviderId', 'model'] as const

// With a large font on a phone, a label of five words is wider than the card, so it wraps and the button grows.
const wrappingButton = 'h-auto min-h-8 max-w-full py-1.5 whitespace-normal'

function FieldMessage({ id, message }: { id: string; message?: string }) {
  if (!message) return null
  return (
    <p id={id} className="text-sm text-destructive">
      {message}
    </p>
  )
}

function EngineForm({ engine, providers }: { engine: TranscriptionEngineDto; providers: ModelProviderDto[] }) {
  const choose = useChooseTranscriptionEngine()
  const form = useForm<EngineChoice>({
    resolver: zodResolver(schema),
    defaultValues: { modelProviderId: engine.modelProviderId ?? '', model: engine.model ?? '' },
  })
  const serverErrors = useServerErrors(form, FIELDS)
  const { errors } = form.formState

  const handleSubmit = form.handleSubmit((values) => {
    serverErrors.clear()
    choose.mutate(values, { onError: serverErrors.show })
  })

  const handleKeepOnServer = () => {
    serverErrors.clear()
    form.clearErrors()
    choose.mutate({ modelProviderId: null, model: null }, { onError: serverErrors.show })
  }

  return (
    <form onSubmit={(event) => void handleSubmit(event)} noValidate className="flex flex-col gap-4">
      <Controller
        control={form.control}
        name="modelProviderId"
        render={({ field }) => (
          <Select
            placeholder="Pick a model provider"
            value={field.value || null}
            onChange={(key) => {
              if (typeof key === 'string') field.onChange(key)
            }}
            isInvalid={Boolean(errors.modelProviderId)}
            className="flex w-full flex-col gap-2"
          >
            <Label>Model provider for audio</Label>
            <SelectTrigger ref={field.ref} className="w-full">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              {providers.map((provider) => (
                <SelectItem key={provider.id} id={provider.id}>
                  {provider.name}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        )}
      />
      <FieldMessage id="audio-provider-error" message={errors.modelProviderId?.message} />

      <div className="flex flex-col gap-2">
        <Label htmlFor="audio-model">Model for audio</Label>
        <Input
          id="audio-model"
          autoComplete="off"
          aria-invalid={Boolean(errors.model)}
          aria-describedby={errors.model ? 'audio-model-error audio-model-hint' : 'audio-model-hint'}
          {...form.register('model')}
        />
        <p id="audio-model-hint" className="text-sm text-muted-foreground">
          The name that the provider gives the model, such as gpt-transcribe, whisper-1, or whisper-large-v3-turbo.
        </p>
        <FieldMessage id="audio-model-error" message={errors.model?.message} />
      </div>

      <RefusedSaveAlert problem={serverErrors.problem} />

      <div className="flex flex-wrap gap-2">
        <Button type="submit" className={wrappingButton} isPending={choose.isPending && Boolean(choose.variables?.modelProviderId)}>
          Send audio to this provider
        </Button>
        {engine.engine === 'Cloud' && (
          <Button variant="outline" className={wrappingButton} isPending={choose.isPending && !choose.variables?.modelProviderId} onPress={handleKeepOnServer}>
            Keep audio on this server
          </Button>
        )}
      </div>
    </form>
  )
}

export function TranscriptionEngineCard({ canChoose }: { canChoose: boolean }) {
  const engine = useTranscriptionEngine()
  const providers = useModelProviders(canChoose)
  const audioProviders = providers.data?.items.filter((provider) => TAKES_AUDIO[provider.kind]) ?? []

  return (
    <Card>
      <CardHeader>
        <CardTitle className="text-lg">
          <h2>Audio to text</h2>
        </CardTitle>
      </CardHeader>
      <CardContent className="flex flex-col gap-4">
        {engine.isPending && (
          <p role="status" aria-label="Loading the engine for audio">
            Loading the engine for audio
          </p>
        )}
        {engine.isError && (
          <QueryError title="The engine for audio did not load." error={engine.error} onRetry={() => void engine.refetch()} />
        )}
        {engine.isSuccess && (
          <>
            <dl className="grid gap-x-4 gap-y-1 text-sm sm:grid-cols-[max-content_1fr]">
              <dt className="text-muted-foreground">Engine</dt>
              <dd className="wrap-anywhere max-sm:mb-2">
                {engine.data.engine === 'Cloud' ? engine.data.modelProviderName : 'This server'}
              </dd>
              {engine.data.engine === 'Cloud' && (
                <>
                  <dt className="text-muted-foreground">Model</dt>
                  <dd className="wrap-anywhere max-sm:mb-2">{engine.data.model}</dd>
                </>
              )}
            </dl>
            <p className="text-sm wrap-anywhere">{engine.data.text}</p>
            {canChoose && audioProviders.length > 0 && <EngineForm engine={engine.data} providers={audioProviders} />}
          </>
        )}
      </CardContent>
    </Card>
  )
}
