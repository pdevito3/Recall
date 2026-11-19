import { describe, expect, test } from 'vitest'

import type { CorrectionDto } from '@/api/generated/models/types/correction-dto'

import { addCorrectionSchema, bodyOf, editCorrectionSchema, EMPTY_CORRECTION, isKnownWord, matching, valuesOf, withTerm } from './correction-form-schema'

const aCorrection = (id: string, canonicalTerm: string, mishearings: string[]): CorrectionDto => ({
  id,
  canonicalTerm,
  mishearings,
  hardReplace: false,
  version: 1,
})

const messagesOf = (values: Partial<typeof EMPTY_CORRECTION>, schema = addCorrectionSchema) => {
  const result = schema.safeParse({ ...EMPTY_CORRECTION, ...values })
  return result.success ? [] : result.error.issues.map((issue) => issue.message)
}

describe('the limits of the correction form', () => {
  test('takes a term of 200 characters, and counts an emoji as one', () => {
    // Arrange, Act, and Assert
    expect(messagesOf({ canonicalTerm: '😀'.repeat(200), mishearings: 'smile' })).toEqual([])
    expect(messagesOf({ canonicalTerm: 'a'.repeat(201), mishearings: 'a' })).toEqual(['Use 200 characters or fewer.'])
  })

  test('does not count the spaces that the Server trims from the term', () => {
    // Arrange, Act, and Assert
    expect(messagesOf({ canonicalTerm: ` ${'a'.repeat(200)} `, mishearings: 'a' })).toEqual([])
  })

  test('refuses a blank term with the words of the Server', () => {
    // Arrange, Act, and Assert
    expect(messagesOf({ canonicalTerm: '   ', mishearings: 'a' })).toEqual(['Please provide a term.'])
  })

  test('refuses a correction with no mishearing, also with only blank lines', () => {
    // Arrange, Act, and Assert
    expect(messagesOf({ canonicalTerm: 'GRDB', mishearings: ' \n\n ' })).toEqual([
      'Please provide at least one mishearing. To keep a word with no mishearings, add it under Known words.',
    ])
  })

  test('refuses a new correction whose only mishearing is the term in another case, as the Server drops it', () => {
    // Arrange, Act, and Assert
    expect(messagesOf({ canonicalTerm: ' GRDB ', mishearings: 'grdb\nGRDB' })).toEqual([
      'A mishearing must differ from the term. To keep a word with no mishearings, add it under Known words.',
    ])
    expect(messagesOf({ canonicalTerm: 'GRDB', mishearings: 'grdb\ngrid b' })).toEqual([])
  })

  test('takes an edit with no mishearing, which makes the term a known word', () => {
    // Arrange, Act, and Assert
    expect(messagesOf({ canonicalTerm: 'GRDB', mishearings: '' }, editCorrectionSchema)).toEqual([])
  })

  test('refuses an edit whose only mishearing is the term in another case, as the Server drops it', () => {
    // Arrange, Act, and Assert
    expect(messagesOf({ canonicalTerm: 'GRDB', mishearings: 'grdb' }, editCorrectionSchema)).toEqual([
      'A mishearing must differ from the term. Clear them to keep the term as a known word.',
    ])
    expect(messagesOf({ canonicalTerm: 'GRDB', mishearings: 'grdb\ngrid b' }, editCorrectionSchema)).toEqual([])
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

describe('a known word', () => {
  const corrections = [aCorrection('cor_1', 'Café Lumière', []), aCorrection('cor_2', 'GRDB', ['Grid B'])]

  test('is a correction with no mishearings', () => {
    // Arrange, Act, and Assert
    expect(corrections.map(isKnownWord)).toEqual([true, false])
  })

  test('finds a term with no regard to case, but not to accents', () => {
    // Arrange, Act, and Assert
    expect(withTerm(corrections, 'café lumière')?.id).toBe('cor_1')
    expect(withTerm(corrections, 'grdb')?.id).toBe('cor_2')
    expect(withTerm(corrections, 'Cafe Lumiere')).toBeUndefined()
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
