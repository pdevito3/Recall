import * as z from 'zod'

import type { CorrectionDto } from '@/api/generated/models/types/correction-dto'
import type { CorrectionForCreationDto } from '@/api/generated/models/types/correction-for-creation-dto'
import { correctionForCreationDtoSchema } from '@/api/generated/models/zod/correction-for-creation-dto'
import { correctionForUpdateDtoSchema } from '@/api/generated/models/zod/correction-for-update-dto'
import { maxLengthOf, stringWithMaxLengthOf } from '@/lib/api-contract'

export const termMaxLength = maxLengthOf(correctionForCreationDtoSchema.shape.canonicalTerm)
export const mishearingMaxLength = maxLengthOf(correctionForCreationDtoSchema.shape.mishearings.element)

export const CORRECTION_FIELDS = ['canonicalTerm', 'mishearings', 'hardReplace'] as const

/** The mishearings of the form, one on each line. */
export function linesOf(text: string): string[] {
  return text
    .split('\n')
    .map((line) => line.trim())
    .filter((line) => line.length > 0)
}

export const correctionFormSchema = z.object({
  canonicalTerm: stringWithMaxLengthOf(correctionForCreationDtoSchema.shape.canonicalTerm).refine(
    (term) => term.trim().length > 0,
    'Please provide a term.',
  ),
  mishearings: z
    .string()
    .refine(
      (text) => linesOf(text).every((line) => [...line].length <= mishearingMaxLength),
      `Use ${mishearingMaxLength} characters or fewer on each line.`,
    ),
  hardReplace: z.boolean(),
  version: correctionForUpdateDtoSchema.shape.version,
})

export type CorrectionFormValues = z.infer<typeof correctionFormSchema>

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
  return { canonicalTerm: values.canonicalTerm.trim(), mishearings: linesOf(values.mishearings), hardReplace: values.hardReplace }
}

const folded = (text: string) => text.normalize('NFD').replace(/\p{M}/gu, '').toLocaleLowerCase()

/** The Corrections whose term or a mishearing holds `search`, with no regard to case or accents. */
export function matching(corrections: CorrectionDto[], search: string): CorrectionDto[] {
  const text = folded(search.trim())
  if (!text) return corrections
  return corrections.filter(
    (correction) =>
      folded(correction.canonicalTerm).includes(text) || correction.mishearings.some((heard) => folded(heard).includes(text)),
  )
}
