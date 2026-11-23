import { describe, expect, test } from 'vitest'

import { bodyOf, detailsOf, personDetailsSchema } from './person-details-form-schema'

const messagesOf = (values: { fullName?: string; notes?: string }) => {
  const result = personDetailsSchema.safeParse({ fullName: '', notes: '', version: 1, ...values })
  return result.success ? [] : result.error.issues.map((issue) => issue.message)
}

describe('the limits of the details form', () => {
  test('takes a full name of 200 characters, and counts an emoji as one', () => {
    // Arrange, Act, and Assert
    expect(messagesOf({ fullName: '😀'.repeat(200) })).toEqual([])
    expect(messagesOf({ fullName: 'a'.repeat(201) })).toEqual(['Use 200 characters or fewer.'])
  })

  test('takes notes of 1000 characters, and does not count the spaces that the Server trims', () => {
    // Arrange, Act, and Assert
    expect(messagesOf({ notes: ` ${'a'.repeat(1000)} ` })).toEqual([])
    expect(messagesOf({ notes: 'a'.repeat(1001) })).toEqual(['Use 1000 characters or fewer.'])
  })
})

describe('the body of a details save', () => {
  test('sends a blank detail as null, so that the save clears it, and keeps the version', () => {
    // Arrange
    const values = detailsOf({ id: 'per_1', label: 'Jane', fullName: 'Jane Park', notes: null, version: 4 })

    // Act
    const body = bodyOf({ ...values, fullName: '  ', notes: " Sam's teacher " })

    // Assert
    expect(body).toEqual({ fullName: null, notes: "Sam's teacher", version: 4 })
  })
})
