import * as z from 'zod'

import type { LoginProviderDto } from '@/api/generated/models/types/login-provider-dto'
import type { LoginProviderForCreationDto } from '@/api/generated/models/types/login-provider-for-creation-dto'
import type { LoginProviderForTestDto } from '@/api/generated/models/types/login-provider-for-test-dto'
import type { LoginProviderForUpdateDto } from '@/api/generated/models/types/login-provider-for-update-dto'
import { loginProviderForCreationDtoSchema } from '@/api/generated/models/zod/login-provider-for-creation-dto'
import { loginProviderForUpdateDtoSchema } from '@/api/generated/models/zod/login-provider-for-update-dto'
import { stringWithMaxLengthOf } from '@/lib/api-contract'

const contract = loginProviderForCreationDtoSchema.shape

// The Server checks the rest of each field (IDP-2), and its message goes on the field.
export const loginProviderFormSchema = z.object({
  id: stringWithMaxLengthOf(contract.id),
  displayName: stringWithMaxLengthOf(contract.displayName),
  issuer: stringWithMaxLengthOf(contract.issuer),
  clientId: stringWithMaxLengthOf(contract.clientId),
  clientSecret: stringWithMaxLengthOf(contract.clientSecret),
  allowHttp: z.boolean(),
  caCertificatePath: stringWithMaxLengthOf(contract.caCertificatePath.unwrap().unwrap()),
  version: loginProviderForUpdateDtoSchema.shape.version,
})

export type LoginProviderFormValues = z.infer<typeof loginProviderFormSchema>

/** The fields in the order of the form, so the focus goes to the first refused one. */
export const LOGIN_PROVIDER_FIELDS = [
  'id',
  'displayName',
  'issuer',
  'clientId',
  'clientSecret',
  'allowHttp',
  'caCertificatePath',
] as const satisfies readonly (keyof LoginProviderFormValues)[]

/** The fields that a save of another person can change, for the reload after a stale save. The secret is never read back. */
export const SAVED_FIELDS = ['displayName', 'clientId', 'allowHttp', 'caCertificatePath'] as const

export const EMPTY_LOGIN_PROVIDER: LoginProviderFormValues = {
  id: '',
  displayName: '',
  issuer: '',
  clientId: '',
  clientSecret: '',
  allowHttp: false,
  caCertificatePath: '',
  version: 0,
}

/** No answer holds the secret (IDP-4), so the edit form starts with it empty. */
export function valuesOf(provider: LoginProviderDto): LoginProviderFormValues {
  return {
    id: provider.id,
    displayName: provider.displayName,
    issuer: provider.issuer,
    clientId: provider.clientId,
    clientSecret: '',
    allowHttp: provider.allowHttp,
    caCertificatePath: provider.caCertificatePath ?? '',
    version: provider.version,
  }
}

const pathOf = (values: LoginProviderFormValues) => values.caCertificatePath.trim() || null

export function creationBodyOf(values: LoginProviderFormValues): LoginProviderForCreationDto {
  return {
    id: values.id.trim(),
    displayName: values.displayName.trim(),
    issuer: values.issuer.trim(),
    clientId: values.clientId.trim(),
    clientSecret: values.clientSecret,
    allowHttp: values.allowHttp,
    caCertificatePath: pathOf(values),
  }
}

/** The issuer is fixed (IDP-6), so the body leaves it out. An empty secret keeps the stored one (IDP-4). */
export function updateBodyOf(values: LoginProviderFormValues): LoginProviderForUpdateDto {
  return {
    displayName: values.displayName.trim(),
    clientId: values.clientId.trim(),
    clientSecret: values.clientSecret === '' ? null : values.clientSecret,
    allowHttp: values.allowHttp,
    caCertificatePath: pathOf(values),
    version: values.version,
  }
}

export function testBodyOf(values: LoginProviderFormValues): LoginProviderForTestDto {
  return { issuer: values.issuer.trim(), allowHttp: values.allowHttp, caCertificatePath: pathOf(values) }
}
