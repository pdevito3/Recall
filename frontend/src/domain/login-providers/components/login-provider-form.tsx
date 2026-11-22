import { zodResolver } from '@hookform/resolvers/zod'
import { useQueryClient } from '@tanstack/react-query'
import { useRef, useState, type ComponentProps } from 'react'
import { Controller, useForm } from 'react-hook-form'

import type { LoginProviderDto } from '@/api/generated/models/types/login-provider-dto'
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
import { useLoadLatest } from '@/hooks/use-load-latest'
import { useServerErrors } from '@/hooks/use-server-errors'
import { focusFirstError } from '@/lib/focus-first-error'

import { loginProviderListOptions, useAddLoginProvider, useTestLoginProvider, useUpdateLoginProvider } from '../apis/login-providers'
import {
  creationBodyOf,
  EMPTY_LOGIN_PROVIDER,
  LOGIN_PROVIDER_FIELDS,
  loginProviderFormSchema,
  SAVED_FIELDS,
  testBodyOf,
  updateBodyOf,
  valuesOf,
  type LoginProviderFormValues,
} from './login-provider-form-schema'

const FIELD_LABELS: Record<(typeof SAVED_FIELDS)[number], string> = {
  displayName: 'Name',
  clientId: 'Client ID',
  allowHttp: 'Allow HTTP',
  caCertificatePath: 'CA file path',
}

interface TextFieldProps extends ComponentProps<typeof Input> {
  id: string
  label: string
  hint?: string
  error?: string
}

function TextField({ id, label, hint, error, ...input }: TextFieldProps) {
  const described = [hint && `${id}-hint`, error && `${id}-error`].filter(Boolean).join(' ')
  return (
    <div className="flex flex-col gap-2">
      <Label htmlFor={id}>{label}</Label>
      <Input id={id} autoComplete="off" spellCheck={false} aria-invalid={Boolean(error)} aria-describedby={described || undefined} {...input} />
      {hint && (
        <p id={`${id}-hint`} className="text-sm text-muted-foreground">
          {hint}
        </p>
      )}
      {error && (
        <p id={`${id}-error`} className="text-sm text-destructive wrap-anywhere">
          {error}
        </p>
      )}
    </div>
  )
}

/** The result of a test shows at the end of a tall form, under the footer of the dialog, so it scrolls into view when it comes. */
const showWhole = (node: HTMLElement | null) => node?.scrollIntoView({ block: 'nearest' })

interface LoginProviderFormProps {
  /** The row to edit. With none, the form adds a provider. */
  provider?: LoginProviderDto
  onSaved: () => void
}

export function LoginProviderForm({ provider, onSaved }: LoginProviderFormProps) {
  const queryClient = useQueryClient()
  const add = useAddLoginProvider()
  const update = useUpdateLoginProvider(provider?.id ?? '')
  const test = useTestLoginProvider()
  const form = useForm<LoginProviderFormValues>({
    resolver: zodResolver(loginProviderFormSchema),
    defaultValues: provider ? valuesOf(provider) : EMPTY_LOGIN_PROVIDER,
    shouldFocusError: false,
  })
  const serverErrors = useServerErrors(form, LOGIN_PROVIDER_FIELDS)
  const latest = useLoadLatest(form, SAVED_FIELDS)
  const { errors } = form.formState
  const save = provider ? update : add
  // A second submit in the same task still reads isPending as false, so only a ref stops a second add.
  const isSending = useRef(false)
  const [latestFailed, setLatestFailed] = useState(false)

  const handleSubmit = form.handleSubmit(async (values) => {
    if (isSending.current) return
    isSending.current = true
    serverErrors.clear()
    latest.clear()
    setLatestFailed(false)
    try {
      if (provider) await update.mutateAsync(updateBodyOf(values))
      else await add.mutateAsync(creationBodyOf(values))
    } catch (error) {
      serverErrors.show(error)
      return
    } finally {
      isSending.current = false
    }
    Notification.success(provider ? 'Login provider saved' : 'Login provider added')
    onSaved()
  }, focusFirstError(form, LOGIN_PROVIDER_FIELDS))

  const handleTest = () => {
    serverErrors.clear()
    form.clearErrors()
    test.mutate(testBodyOf(form.getValues()), { onError: serverErrors.show })
  }

  const handleLoadLatest = async () => {
    if (!provider) return
    const loaded = await latest.load(async () => {
      const list = await queryClient.fetchQuery({ ...loginProviderListOptions, staleTime: 0 }).catch(() => undefined)
      const row = list?.items.find((item) => item.id === provider.id)
      // No answer holds the secret, so the secret that the Admin typed stays.
      return row && { ...valuesOf(row), clientSecret: form.getValues('clientSecret') }
    })
    setLatestFailed(!loaded)
    if (loaded) serverErrors.clear()
  }

  // A result of Test describes the values that it sent, so a change to one of them clears it.
  const discoveryField = (name: 'issuer' | 'caCertificatePath') => form.register(name, { onChange: () => test.reset() })

  const secretHint = !provider
    ? 'The secret that the provider gave Recall. The server encrypts it, and no page shows it again.'
    : provider.secretState === 'Unreadable'
      ? 'The server cannot read the saved secret. Enter it again.'
      : 'Leave this blank to keep the saved secret.'

  return (
    <form onSubmit={(event) => void handleSubmit(event)} noValidate className="flex flex-col gap-4">
      <ResponsiveDialogHeader>
        <ResponsiveDialogTitle>{provider ? `Edit ${provider.displayName}` : 'Add a login provider'}</ResponsiveDialogTitle>
        <ResponsiveDialogDescription>
          People sign in with an account at this OpenID Connect provider.
        </ResponsiveDialogDescription>
      </ResponsiveDialogHeader>

      {!provider && (
        <TextField
          id="login-provider-id"
          label="ID"
          hint="The name of the provider in the sign-in URLs: letters, digits, dashes, or underscores. It cannot change."
          error={errors.id?.message}
          {...form.register('id')}
        />
      )}
      <TextField
        id="login-provider-name"
        label="Name"
        hint="The sign-in page shows this name."
        error={errors.displayName?.message}
        {...form.register('displayName')}
      />
      <TextField
        id="login-provider-issuer"
        label="Issuer URL"
        type="url"
        readOnly={Boolean(provider)}
        className={provider ? 'bg-muted text-muted-foreground dark:bg-muted' : undefined}
        hint={provider ? 'The issuer of a provider cannot change. Add a new provider instead.' : 'Such as https://id.example.com/realms/home.'}
        error={errors.issuer?.message}
        {...discoveryField('issuer')}
      />
      <TextField id="login-provider-client-id" label="Client ID" error={errors.clientId?.message} {...form.register('clientId')} />
      <TextField
        id="login-provider-client-secret"
        label="Client secret"
        type="password"
        // A password manager fills a saved password into a password field, and a value there replaces the stored secret (IDP-4).
        autoComplete="new-password"
        data-1p-ignore
        data-lpignore="true"
        hint={secretHint}
        error={errors.clientSecret?.message}
        {...form.register('clientSecret')}
      />
      <Controller
        control={form.control}
        name="allowHttp"
        render={({ field }) => (
          <div className="flex flex-col gap-1">
            <Checkbox
              className="max-md:tap-area-y"
              isSelected={field.value}
              onChange={(isSelected) => {
                field.onChange(isSelected)
                test.reset()
              }}
              inputRef={field.ref}
              aria-describedby={errors.allowHttp ? 'login-provider-allow-http-hint login-provider-allow-http-error' : 'login-provider-allow-http-hint'}
            >
              Allow HTTP
            </Checkbox>
            <p id="login-provider-allow-http-hint" className="text-sm text-muted-foreground">
              Only for a provider on your own network with no HTTPS. Over HTTP, another device on the network can read the client secret.
            </p>
            {errors.allowHttp?.message && (
              <p id="login-provider-allow-http-error" className="text-sm text-destructive wrap-anywhere">
                {errors.allowHttp.message}
              </p>
            )}
          </div>
        )}
      />
      <TextField
        id="login-provider-ca-path"
        label="CA file path"
        hint="Optional. A PEM file on the server with the certificates of a private CA that signed the certificate of the provider."
        error={errors.caCertificatePath?.message}
        {...discoveryField('caCertificatePath')}
      />

      <div className="flex flex-col gap-2">
        <Button variant="outline" className="self-start" isPending={test.isPending} onPress={handleTest}>
          Test the issuer
        </Button>
        {test.data?.ok && (
          <p ref={showWhole} role="status" className="text-sm wrap-anywhere">
            The provider answered. Its issuer is {test.data.issuer}.
          </p>
        )}
        {test.data?.failure && (
          <div ref={showWhole} role="alert" className="rounded-lg border border-destructive/20 bg-destructive/10 p-3 text-sm text-destructive">
            <p className="font-medium">The test failed.</p>
            <p className="wrap-anywhere">{test.data.failure.message}</p>
          </div>
        )}
      </div>

      <RefusedSaveAlert problem={serverErrors.problem} />
      <StaleVersionAlert
        noun="login provider"
        problem={serverErrors.problem}
        isLoadingLatest={latest.isLoading}
        onLoadLatest={() => void handleLoadLatest()}
      />
      {latestFailed && (
        <div role="alert" className="rounded-lg border border-destructive/20 bg-destructive/10 p-4 text-sm text-destructive">
          <p className="font-medium">The latest login provider did not load.</p>
          <p>Check the connection, then press Load the latest again.</p>
        </div>
      )}
      <LatestLoadedNote noun="login provider" changed={latest.changed} labels={FIELD_LABELS} />

      <ResponsiveDialogFooter>
        <ResponsiveDialogClose>Cancel</ResponsiveDialogClose>
        <Button type="submit" isPending={save.isPending} isDisabled={latest.isLoading}>
          {provider ? 'Save login provider' : 'Add login provider'}
        </Button>
      </ResponsiveDialogFooter>
    </form>
  )
}
