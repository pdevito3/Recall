import { CalendarDate } from '@internationalized/date'
import { describe, expect, test } from 'vitest'

import { parseTimeZone, todayAt } from '@/lib/zoned-time'

import {
  bodyOf,
  EMPTY_TOKEN,
  EXPIRY_MESSAGE,
  journalAccessAfter,
  tokenFormSchema,
  type TokenFormValues,
} from './token-form-schema'

const today = new CalendarDate(2026, 11, 16)
const pagoPago = parseTimeZone('Pacific/Pago_Pago')
const kiritimati = parseTimeZone('Pacific/Kiritimati')

const messagesOf = (values: Partial<TokenFormValues>) => {
  const result = tokenFormSchema(today).safeParse({ ...EMPTY_TOKEN, name: 'Laptop', ...values })
  return result.success ? [] : result.error.issues.map((issue) => issue.message)
}

describe('the expiry of a new token', () => {
  test('@PAT-20 the default is the end of the day 90 days after the day of the browser', () => {
    // Arrange
    const instant = new Date('2026-11-16T05:00:00Z')

    // Act
    const westOfUtc = bodyOf({ ...EMPTY_TOKEN, name: 'Laptop' }, todayAt(pagoPago, instant), pagoPago)
    const eastOfUtc = bodyOf({ ...EMPTY_TOKEN, name: 'Laptop' }, todayAt(kiritimati, instant), kiritimati)

    // Assert
    expect(westOfUtc).toEqual({ name: 'Laptop', permissions: [], readJournal: false, writeJournal: false, jobTools: false, expiresAt: '2027-02-14T10:59:59.999Z' })
    expect(eastOfUtc.expiresAt).toBe('2027-02-14T09:59:59.999Z')
    expect(eastOfUtc.neverExpires).toBeUndefined()
  })

  test('@PAT-8 Never sends neverExpires and no expiresAt', () => {
    // Arrange, Act
    const body = bodyOf({ ...EMPTY_TOKEN, name: 'Laptop', expiry: 'never' }, today, pagoPago)

    // Assert
    expect(body).toEqual({ name: 'Laptop', permissions: [], readJournal: false, writeJournal: false, jobTools: false, neverExpires: true })
  })

  test('@PAT-21 the body sends the job tools as picked', () => {
    // Arrange, Act
    const picked = bodyOf({ ...EMPTY_TOKEN, name: 'Claude Code', expiry: 'never', jobTools: true }, today, pagoPago)
    const notPicked = bodyOf({ ...EMPTY_TOKEN, name: 'Laptop', expiry: 'never' }, today, pagoPago)

    // Assert
    expect(picked).toEqual({ name: 'Claude Code', permissions: [], readJournal: false, writeJournal: false, jobTools: true, neverExpires: true })
    expect(notPicked.jobTools).toBe(false)
  })

  test('@PAT-8 a picked date ends at the end of that day in the zone of the browser', () => {
    // Arrange, Act
    const body = bodyOf({ ...EMPTY_TOKEN, name: 'Laptop', expiry: 'date', expiresAt: new CalendarDate(2027, 1, 31) }, today, kiritimati)

    // Assert
    expect(body.expiresAt).toBe('2027-01-31T09:59:59.999Z')
    expect(body.neverExpires).toBeUndefined()
  })

  test('@PAT-8 refuses a date in the past, a missing date, and the year of never, with the words of the Server', () => {
    // Arrange, Act, and Assert
    expect(messagesOf({ expiry: 'date', expiresAt: new CalendarDate(2026, 11, 15) })).toEqual([EXPIRY_MESSAGE])
    expect(messagesOf({ expiry: 'date', expiresAt: null })).toEqual([EXPIRY_MESSAGE])
    expect(messagesOf({ expiry: 'date', expiresAt: new CalendarDate(9999, 12, 1) })).toEqual([EXPIRY_MESSAGE])
    expect(messagesOf({ expiry: 'date', expiresAt: today })).toEqual([])
    expect(messagesOf({ expiry: 'never', expiresAt: new CalendarDate(2026, 11, 15) })).toEqual([])
  })
})

describe('the journal access of a new token', () => {
  test('@PAT-5 Change my journal ticks Read my journal, and a read that goes off takes the change with it', () => {
    // Arrange
    const none = { readJournal: false, writeJournal: false }

    // Act
    const changed = journalAccessAfter(none, 'writeJournal', true)
    const readOff = journalAccessAfter(changed, 'readJournal', false)
    const changeOff = journalAccessAfter(changed, 'writeJournal', false)

    // Assert
    expect(changed).toEqual({ readJournal: true, writeJournal: true })
    expect(readOff).toEqual(none)
    expect(changeOff).toEqual({ readJournal: true, writeJournal: false })
  })

  test('@PAT-5 refuses a change with no read, with the words of the Server', () => {
    // Arrange, Act, and Assert
    expect(messagesOf({ writeJournal: true })).toEqual(['A token that changes your journal must also read it.'])
  })
})

describe('the name of a new token', () => {
  test('takes 100 characters and counts an emoji as one, but not 101', () => {
    // Arrange, Act, and Assert
    expect(messagesOf({ name: '😀'.repeat(100) })).toEqual([])
    expect(messagesOf({ name: ` ${'a'.repeat(100)} ` })).toEqual([])
    expect(messagesOf({ name: 'a'.repeat(101) })).toEqual(['A name can have no more than 100 characters.'])
  })

  test('refuses a blank name with the words of the Server', () => {
    // Arrange, Act, and Assert
    expect(messagesOf({ name: '   ' })).toEqual(['Please give the token a name.'])
  })

  test('sends the name with no spaces at its ends', () => {
    // Arrange, Act
    const body = bodyOf({ ...EMPTY_TOKEN, name: '  Laptop  ' }, today, pagoPago)

    // Assert
    expect(body.name).toBe('Laptop')
  })
})
