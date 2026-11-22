import { describe, expect, test } from 'vitest'

import { loginProviderForCreationDtoSchema } from '@/api/generated/models/zod/login-provider-for-creation-dto'
import { maxLengthOf } from '@/lib/api-contract'

import { EMPTY_LOGIN_PROVIDER, loginProviderFormSchema, updateBodyOf } from './login-provider-form-schema'

const contract = loginProviderForCreationDtoSchema.shape
const limits = {
  id: maxLengthOf(contract.id),
  displayName: maxLengthOf(contract.displayName),
  issuer: maxLengthOf(contract.issuer),
  clientId: maxLengthOf(contract.clientId),
  clientSecret: maxLengthOf(contract.clientSecret),
  caCertificatePath: maxLengthOf(contract.caCertificatePath.unwrap().unwrap()),
}

describe('the login provider form schema', () => {
  for (const [field, limit] of Object.entries(limits)) {
    test(`takes ${field} at the limit of the contract, counted in characters, and refuses one more`, () => {
      // Arrange
      const atLimit = { ...EMPTY_LOGIN_PROVIDER, [field]: '😀'.repeat(limit) }
      const overLimit = { ...EMPTY_LOGIN_PROVIDER, [field]: '😀'.repeat(limit + 1) }

      // Act
      const taken = loginProviderFormSchema.safeParse(atLimit)
      const refused = loginProviderFormSchema.safeParse(overLimit)

      // Assert
      expect(taken.success).toBe(true)
      expect(refused.error?.issues.map((issue) => issue.path)).toEqual([[field]])
    })
  }

  test('an update sends no issuer, and no secret when the field is empty', () => {
    // Arrange
    const values = { ...EMPTY_LOGIN_PROVIDER, displayName: ' Home ', issuer: 'https://id.example.com', clientId: 'recall', version: 3 }

    // Act
    const body = updateBodyOf(values)

    // Assert
    expect(body).toEqual({ displayName: 'Home', clientId: 'recall', clientSecret: null, allowHttp: false, caCertificatePath: null, version: 3 })
  })
})
