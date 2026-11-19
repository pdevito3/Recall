import * as z from 'zod'

import type { CorrectionDto } from '@/api/generated/models/types/correction-dto'
import type { CorrectionForCreationDto } from '@/api/generated/models/types/correction-for-creation-dto'
import { correctionForCreationDtoSchema } from '@/api/generated/models/zod/correction-for-creation-dto'
import { correctionForUpdateDtoSchema } from '@/api/generated/models/zod/correction-for-update-dto'
import { maxLengthOf } from '@/lib/api-contract'

export const termMaxLength = maxLengthOf(correctionForCreationDtoSchema.shape.canonicalTerm)
export const mishearingMaxLength = maxLengthOf(correctionForCreationDtoSchema.shape.mishearings.element)

export const CORRECTION_FIELDS = ['canonicalTerm', 'mishearings', 'hardReplace'] as const

function mishearingsOf(text: string): string[] {
  return text
    .split('\n')
    .map((line) => line.trim())
    .filter((line) => line.length > 0)
}

const sameIgnoringCase = (a: string, b: string) => a.toUpperCase() === b.toUpperCase()

const correctionFields = z.object({
  canonicalTerm: z
    .string()
    .refine((term) => term.trim().length > 0, 'Please provide a term.')
    .refine((term) => [...term.trim()].length <= termMaxLength, `Use ${termMaxLength} characters or fewer.`),
  mishearings: z
    .string()
    .refine(
      (text) => mishearingsOf(text).every((line) => [...line].length <= mishearingMaxLength),
      `Use ${mishearingMaxLength} characters or fewer on each line.`,
    ),
  hardReplace: z.boolean(),
  version: correctionForUpdateDtoSchema.shape.version,
})

/** The Server drops a mishearing that is the term, with no regard to case. */
const isOnlyTheTerm = (lines: string[], term: string) =>
  lines.length > 0 && lines.every((line) => sameIgnoringCase(line, term.trim()))

/** An edit can clear the mishearings: the Correction then is a known word. */
export const editCorrectionSchema = correctionFields.superRefine((values, context) => {
  if (isOnlyTheTerm(mishearingsOf(values.mishearings), values.canonicalTerm)) {
    context.addIssue({
      code: 'custom',
      path: ['mishearings'],
      message: 'A mishearing must differ from the term. Clear them to keep the term as a known word.',
    })
  }
})

/** A new Correction needs a mishearing that the Server keeps. */
export const addCorrectionSchema = correctionFields.superRefine((values, context) => {
  const lines = mishearingsOf(values.mishearings)
  if (lines.length === 0) {
    context.addIssue({
      code: 'custom',
      path: ['mishearings'],
      message: 'Please provide at least one mishearing. To keep a word with no mishearings, add it under Known words.',
    })
  } else if (isOnlyTheTerm(lines, values.canonicalTerm)) {
    context.addIssue({
      code: 'custom',
      path: ['mishearings'],
      message: 'A mishearing must differ from the term. To keep a word with no mishearings, add it under Known words.',
    })
  }
})

export type CorrectionFormValues = z.infer<typeof correctionFields>

export const EMPTY_CORRECTION: CorrectionFormValues = { canonicalTerm: '', mishearings: '', hardReplace: false, version: 0 }

export function valuesOf(correction: CorrectionDto): CorrectionFormValues {
  return {
    canonicalTerm: correction.canonicalTerm,
    mishearings: correction.mishearings.join('\n'),
    hardReplace: correction.hardReplace,
    version: correction.version,
  }
}

export function bodyOf(values: CorrectionFormValues): CorrectionForCreationDto {
  return { canonicalTerm: values.canonicalTerm.trim(), mishearings: mishearingsOf(values.mishearings), hardReplace: values.hardReplace }
}

/** A Correction with no mishearings. Cleanup gets its term alone, and the page shows it as a known word. */
export const isKnownWord = (correction: CorrectionDto): boolean => correction.mishearings.length === 0

/** The Correction whose term is the same as the term, with no regard to case. */
export function withTerm(corrections: CorrectionDto[], term: string): CorrectionDto | undefined {
  return corrections.find((correction) => correction.canonicalTerm.localeCompare(term, undefined, { sensitivity: 'accent' }) === 0)
}

const folded = (text: string) => text.normalize('NFD').replace(/\p{M}/gu, '').toLocaleLowerCase()

export function matching(corrections: CorrectionDto[], search: string): CorrectionDto[] {
  const text = folded(search.trim())
  if (!text) return corrections
  return corrections.filter(
    (correction) =>
      folded(correction.canonicalTerm).includes(text) || correction.mishearings.some((heard) => folded(heard).includes(text)),
  )
}
