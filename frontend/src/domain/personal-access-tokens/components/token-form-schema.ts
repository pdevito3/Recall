import { CalendarDate } from '@internationalized/date'
import * as z from 'zod'

import type { PersonalAccessTokenForCreationDto } from '@/api/generated/models/types/personal-access-token-for-creation-dto'
import { personalAccessTokenForCreationDtoSchema } from '@/api/generated/models/zod/personal-access-token-for-creation-dto'
import { maxLengthOf } from '@/lib/api-contract'
import { startOfDayAt, type TimeZone } from '@/lib/zoned-time'

export const nameMaxLength = maxLengthOf(personalAccessTokenForCreationDtoSchema.shape.name)

export const DEFAULT_EXPIRY_DAYS = 90

export const EXPIRY_CHOICES = ['default', 'date', 'never'] as const
export type ExpiryChoice = (typeof EXPIRY_CHOICES)[number]

export const TOKEN_FIELDS = ['name', 'permissions', 'readJournal', 'writeJournal', 'jobTools', 'expiry', 'expiresAt'] as const

export const EXPIRY_MESSAGE = 'Pick an expiry date in the future, or pick Never.'

const isCalendarDate = (value: unknown): value is CalendarDate => value instanceof CalendarDate

export function tokenFormSchema(today: CalendarDate) {
  return z
    .object({
      name: z
        .string()
        .refine((name) => name.trim().length > 0, 'Please give the token a name.')
        .refine((name) => [...name.trim()].length <= nameMaxLength, `A name can have no more than ${nameMaxLength} characters.`),
      permissions: personalAccessTokenForCreationDtoSchema.shape.permissions.unwrap().unwrap(),
      readJournal: z.boolean(),
      writeJournal: z.boolean(),
      jobTools: z.boolean(),
      expiry: z.enum(EXPIRY_CHOICES),
      expiresAt: z.custom<CalendarDate>(isCalendarDate).nullable(),
    })
    .superRefine((values, context) => {
      if (values.writeJournal && !values.readJournal) {
        context.addIssue({ code: 'custom', path: ['writeJournal'], message: 'A token that changes your journal must also read it.' })
      }
      // The Server refuses an expiry at or after 9999-12-31T00:00Z, its instant for never. In some zones the end of a late
      // day of 9999 reaches that instant, so the form refuses the whole year.
      const date = values.expiresAt
      if (values.expiry === 'date' && (!date || date.compare(today) < 0 || date.year >= 9999)) {
        context.addIssue({ code: 'custom', path: ['expiresAt'], message: EXPIRY_MESSAGE })
      }
    })
}

export type TokenFormValues = z.infer<ReturnType<typeof tokenFormSchema>>

export const EMPTY_TOKEN: TokenFormValues = {
  name: '',
  permissions: [],
  readJournal: false,
  writeJournal: false,
  jobTools: false,
  expiry: 'default',
  expiresAt: null,
}

type JournalAccess = Pick<TokenFormValues, 'readJournal' | 'writeJournal'>

/** The Server refuses a change of the journal with no read of it, so a change ticks the read, and a read that goes off takes the change with it. */
export function journalAccessAfter(access: JournalAccess, field: keyof JournalAccess, isSelected: boolean): JournalAccess {
  if (field === 'writeJournal') return { readJournal: isSelected || access.readJournal, writeJournal: isSelected }
  return { readJournal: isSelected, writeJournal: isSelected && access.writeJournal }
}

export const defaultExpiryDate = (today: CalendarDate): CalendarDate => today.add({ days: DEFAULT_EXPIRY_DAYS })

/** The last day of the token in the zone of the browser, or null for a token that never expires. */
export function expiryDateOf(values: Pick<TokenFormValues, 'expiry' | 'expiresAt'>, today: CalendarDate): CalendarDate | null {
  if (values.expiry === 'never') return null
  if (values.expiry === 'date') return values.expiresAt
  return defaultExpiryDate(today)
}

const endOfDayAt = (date: CalendarDate, zone: TimeZone): Date => new Date(startOfDayAt(date.add({ days: 1 }), zone).getTime() - 1)

/** The body of the create. A token works through the whole of its last day in the zone of the browser. */
export function bodyOf(values: TokenFormValues, today: CalendarDate, zone: TimeZone): PersonalAccessTokenForCreationDto {
  const lastDay = expiryDateOf(values, today)
  return {
    name: values.name.trim(),
    permissions: values.permissions,
    readJournal: values.readJournal,
    writeJournal: values.writeJournal,
    jobTools: values.jobTools,
    ...(lastDay ? { expiresAt: endOfDayAt(lastDay, zone).toISOString() } : { neverExpires: true }),
  }
}
