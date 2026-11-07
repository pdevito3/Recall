import { useState } from 'react'

import type { ModelProviderDto } from '@/api/generated/models/types/model-provider-dto'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardAction, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
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
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'

import { useDeleteModelProvider, useTestModelProvider, useUpdateModelProvider } from '../apis/model-providers'
import { KIND_LABELS } from './model-provider-kind'

function savedChange(provider: ModelProviderDto) {
  return { name: provider.name, baseUrl: provider.baseUrl, model: provider.model, makeActive: false, version: provider.version }
}

export function ModelProviderCard({ provider }: { provider: ModelProviderDto }) {
  const test = useTestModelProvider(provider.id)
  const update = useUpdateModelProvider(provider.id)
  const remove = useDeleteModelProvider(provider.id)
  const [newKey, setNewKey] = useState('')
  const [editedUrl, setEditedUrl] = useState<string | null>(null)
  const keyFieldId = `new-key-${provider.id}`
  const urlFieldId = `new-url-${provider.id}`
  const newUrl = editedUrl ?? provider.baseUrl ?? ''
  const urlChanged = newUrl.trim() !== (provider.baseUrl ?? '')
  const urlNeedsKey = urlChanged && provider.keyState !== 'None' && newKey.trim() === ''

  const handleSaveKey = () =>
    update.mutate({ ...savedChange(provider), apiKey: newKey }, { onSuccess: () => setNewKey('') })

  const handleSaveUrl = () =>
    update.mutate(
      { ...savedChange(provider), baseUrl: newUrl.trim() || null, apiKey: newKey.trim() === '' ? undefined : newKey },
      {
        onSuccess: () => {
          setNewKey('')
          setEditedUrl(null)
        },
      },
    )

  return (
    <Card>
      <CardHeader>
        <CardTitle className="flex flex-wrap items-center gap-2 text-lg">
          <h3 className="wrap-anywhere">{provider.name}</h3>
          <Badge variant="outline">{KIND_LABELS[provider.kind]}</Badge>
          {provider.isActive && <Badge>Active</Badge>}
        </CardTitle>
        <CardAction>
          <ResponsiveDialogTrigger>
            <Button variant="destructive" isPending={remove.isPending}>
              Delete
            </Button>
            <ResponsiveDialog>
              <ResponsiveDialogHeader>
                <ResponsiveDialogTitle>Delete {provider.name}?</ResponsiveDialogTitle>
                <ResponsiveDialogDescription>
                  The server forgets its API key. Sessions keep their Cleaned text.
                </ResponsiveDialogDescription>
              </ResponsiveDialogHeader>
              <ResponsiveDialogFooter>
                <ResponsiveDialogClose>Keep model provider</ResponsiveDialogClose>
                <ResponsiveDialogClose variant="destructive" onPress={() => remove.mutate()}>
                  Delete model provider
                </ResponsiveDialogClose>
              </ResponsiveDialogFooter>
            </ResponsiveDialog>
          </ResponsiveDialogTrigger>
        </CardAction>
      </CardHeader>
      <CardContent className="flex flex-col gap-4">
        <dl className="grid gap-x-4 gap-y-1 text-sm sm:grid-cols-[max-content_1fr]">
          {provider.baseUrl && (
            <>
              <dt className="text-muted-foreground">Base URL</dt>
              <dd className="wrap-anywhere max-sm:mb-2">{provider.baseUrl}</dd>
            </>
          )}
          <dt className="text-muted-foreground">Model</dt>
          <dd className="wrap-anywhere max-sm:mb-2">{provider.model ?? 'Test the connection, then pick a model.'}</dd>
          <dt className="text-muted-foreground">API key</dt>
          <dd>
            {provider.keyState === 'Readable' && 'Saved and encrypted'}
            {provider.keyState === 'None' && 'None'}
            {provider.keyState === 'Unreadable' && (
              <span className="text-destructive">The server cannot read this key. Enter it again.</span>
            )}
          </dd>
        </dl>

        <div className="flex flex-wrap gap-2">
          <Button variant="outline" isPending={test.isPending} onPress={() => test.mutate()}>
            Test connection
          </Button>
          {!provider.isActive && (
            <Button
              variant="outline"
              isDisabled={!provider.model}
              isPending={update.isPending}
              onPress={() => update.mutate({ ...savedChange(provider), makeActive: true })}
            >
              Make active
            </Button>
          )}
        </div>

        {!provider.isActive && (
          <p className="text-sm text-muted-foreground">
            {provider.model
              ? 'A Cleanup does not use this model provider until you make it active.'
              : 'A Cleanup does not use this model provider. Pick a model, then make it active.'}
          </p>
        )}

        {test.data?.failure && (
          <div role="alert" className="rounded-lg border border-destructive/20 bg-destructive/10 p-3 text-sm text-destructive">
            <p className="font-medium">The connection failed.</p>
            <p className="wrap-anywhere">{test.data.failure.message}</p>
          </div>
        )}

        {test.data?.ok && (
          <div className="flex flex-col gap-3">
            <p role="status" className="text-sm">
              Connected. The model provider offers {test.data.models.length}{' '}
              {test.data.models.length === 1 ? 'model' : 'models'}.
            </p>
            <Select
              placeholder="Pick a model"
              value={provider.model ?? null}
              onChange={(model) => {
                if (typeof model === 'string') update.mutate({ ...savedChange(provider), model })
              }}
              className="flex w-full flex-col gap-2"
            >
              <Label>Model of {provider.name}</Label>
              <SelectTrigger className="w-full">
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                {test.data.models.map((model) => (
                  <SelectItem key={model.id} id={model.id}>
                    {model.displayName}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          </div>
        )}

        <div className="flex flex-col gap-2">
          <Label htmlFor={urlFieldId}>New URL</Label>
          <div className="flex flex-wrap gap-2">
            <Input
              id={urlFieldId}
              type="url"
              autoComplete="off"
              className="min-w-0 flex-1 basis-48 max-sm:basis-full"
              value={newUrl}
              onChange={(event) => setEditedUrl(event.target.value)}
            />
            <Button variant="outline" isDisabled={!urlChanged || urlNeedsKey} isPending={update.isPending} onPress={handleSaveUrl}>
              Save URL
            </Button>
          </div>
          {urlNeedsKey && <p className="text-sm text-muted-foreground">A new URL needs the API key again. Enter it under New API key.</p>}
        </div>

        <div className="flex flex-col gap-2">
          <Label htmlFor={keyFieldId}>New API key</Label>
          <div className="flex flex-wrap gap-2">
            <Input
              id={keyFieldId}
              type="password"
              autoComplete="off"
              className="min-w-0 flex-1 basis-48"
              value={newKey}
              onChange={(event) => setNewKey(event.target.value)}
            />
            <Button variant="outline" isDisabled={newKey.trim() === '' || urlChanged} isPending={update.isPending} onPress={handleSaveKey}>
              Save key
            </Button>
          </div>
        </div>
      </CardContent>
    </Card>
  )
}
