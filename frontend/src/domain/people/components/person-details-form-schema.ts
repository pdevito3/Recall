import * as z from 'zod'

import type { PersonDetailsDto } from '@/api/generated/models/types/person-details-dto'
import type { PersonDto } from '@/api/generated/models/types/person-dto'
import { personDetailsDtoSchema } from '@/api/generated/models/zod/person-details-dto'
import { maxLengthOf } from '@/lib/api-contract'

export const fullNameMaxLength = maxLengthOf(personDetailsDtoSchema.shape.fullName.unwrap().unwrap())
export const notesMaxLength = maxLengthOf(personDetailsDtoSchema.shape.notes.unwrap().unwrap())

export const PERSON_DETAIL_FIELDS = ['fullName', 'notes'] as const

export const personDetailsSchema = z.object({
  fullName: z
    .string()
    .refine((text) => [...text.trim()].length <= fullNameMaxLength, `Use ${fullNameMaxLength} characters or fewer.`),
  notes: z
    .string()
    .refine((text) => [...text.trim()].length <= notesMaxLength, `Use ${notesMaxLength} characters or fewer.`),
  version: personDetailsDtoSchema.shape.version,
})

export type PersonDetailsFormValues = z.infer<typeof personDetailsSchema>

export function detailsOf(person: PersonDto): PersonDetailsFormValues {
  return { fullName: person.fullName ?? '', notes: person.notes ?? '', version: person.version }
}

/** A blank detail clears it. */
export function bodyOf(values: PersonDetailsFormValues): PersonDetailsDto {
  return { fullName: values.fullName.trim() || null, notes: values.notes.trim() || null, version: values.version }
}
