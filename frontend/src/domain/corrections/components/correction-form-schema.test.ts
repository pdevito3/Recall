import { describe, expect, test } from 'vitest'

import type { CorrectionDto } from '@/api/generated/models/types/correction-dto'

import { bodyOf, correctionFormSchema, EMPTY_CORRECTION, matching, valuesOf } from './correction-form-schema'

const aCorrection = (id: string, canonicalTerm: string, mishearings: string[]): CorrectionDto => ({
  id,
  canonicalTerm,
  mishearings,
  hardReplace: false,
  version: 1,
})

const messagesOf = (values: Partial<typeof EMPTY_CORRECTION>) => {
  const result = correctionFormSchema.safeParse({ ...EMPTY_CORRECTION, ...values })
  return result.success ? [] : result.error.issues.map((issue) => issue.message)
}

describe('the limits of the correction form', () => {
  test('takes a term of 200 characters, and counts an emoji as one', () => {
    // Arrange, Act, and Assert
    expect(messagesOf({ canonicalTerm: '😀'.repeat(200) })).toEqual([])
    expect(messagesOf({ canonicalTerm: 'a'.repeat(201) })).toEqual(['Use 200 characters or fewer.'])
  })

  test('refuses a blank term with the words of the Server', () => {
    // Arrange, Act, and Assert
    expect(messagesOf({ canonicalTerm: '   ' })).toEqual(['Please provide a term.'])
  })

  test('takes a mishearing of 200 characters on each line', () => {
    // Arrange, Act, and Assert
    expect(messagesOf({ canonicalTerm: 'GRDB', mishearings: `${'a'.repeat(200)}\n${'b'.repeat(200)}` })).toEqual([])
    expect(messagesOf({ canonicalTerm: 'GRDB', mishearings: `grid b\n${'b'.repeat(201)}` })).toEqual([
      'Use 200 characters or fewer on each line.',
    ])
  })
})

describe('the body of a save', () => {
  test('sends each line with text as one mishearing', () => {
    // Arrange
    const values = { ...EMPTY_CORRECTION, canonicalTerm: ' GRDB ', mishearings: ' grid b \n\n  \ngreat db', hardReplace: true }

    // Act
    const body = bodyOf(values)

    // Assert
    expect(body).toEqual({ canonicalTerm: 'GRDB', mishearings: ['grid b', 'great db'], hardReplace: true })
  })

  test('opens a correction with one mishearing on each line', () => {
    // Arrange, Act, and Assert
    expect(valuesOf(aCorrection('cor_1', 'GRDB', ['grid b', 'great db']))).toEqual({
      canonicalTerm: 'GRDB',
      mishearings: 'grid b\ngreat db',
      hardReplace: false,
      version: 1,
    })
  })
})

describe('the search of the list', () => {
  const corrections = [aCorrection('cor_1', 'Café Lumière', []), aCorrection('cor_2', 'GRDB', ['Grid B'])]

  test('finds the term with no regard to case or accents', () => {
    // Arrange, Act, and Assert
    expect(matching(corrections, 'lumiere').map(({ id }) => id)).toEqual(['cor_1'])
  })

  test('finds a mishearing', () => {
    // Arrange, Act, and Assert
    expect(matching(corrections, ' grid ').map(({ id }) => id)).toEqual(['cor_2'])
  })

  test('shows each correction for a blank search', () => {
    // Arrange, Act, and Assert
    expect(matching(corrections, '  ')).toHaveLength(2)
  })
})
