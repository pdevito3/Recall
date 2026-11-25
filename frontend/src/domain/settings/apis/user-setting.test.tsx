import { resetLocalTimeZone } from '@internationalized/date'
import { QueryClient } from '@tanstack/react-query'
import { afterEach, beforeEach, expect, test, vi } from 'vitest'
import { commands } from 'vitest/browser'

import type { UserSettingDto } from '@/api/generated/models/types/user-setting-dto'

import { SettingKeys } from './setting.keys'
import { captureZoneOf, locationCaptureIsOn, locationWaitsForAnAnswer, resumeUnsentSetting, settingSaver, userSettingOptions } from './user-setting'

const prefix = 'recall.unsent-setting.person-a.'
const staleKey = {
  change: { value: { locationCaptureEnabled: true }, savedAt: '2026-11-06T00:00:00.000Z' },
  attempt: { base: { values: { locationCaptureEnabled: true, timeZone: 'UTC', requirePeopleProposalApproval: false }, version: 1 }, hadNoReply: false },
  asks: [],
}

let otherTab: BroadcastChannel

// An ask makes the saver read the row. The read must land before its test ends, or it counts as unanswered in the next test.
const rowReads = () => performance.getEntriesByType('resource').filter(({ name }) => new URL(name).pathname === '/api/v1/me/settings').length

beforeEach(() => {
  otherTab = new BroadcastChannel('recall.tabs')
})

afterEach(() => {
  otherTab.close()
  localStorage.clear()
})

test('a tab that opens leaves the key of a live tab, and sends nothing', async () => {
  otherTab.onmessage = ({ data }: MessageEvent<{ kind: string; nonce: string }>) => {
    if (data.kind === 'who') otherTab.postMessage({ kind: 'here', nonce: data.nonce, writer: 'tab-alive' })
  }
  localStorage.setItem(`${prefix}tab-alive.1`, JSON.stringify(staleKey))
  await commands.answerRequests([])

  await resumeUnsentSetting('person-a', new QueryClient())
  await vi.waitFor(() => expect(localStorage.getItem(`${prefix}tab-alive.1`)).not.toBeNull())

  expect(await commands.takeUnansweredRequests()).toEqual([])
})

test('this tab saves no location while a change to off waits for an answer', async () => {
  const row = { timeZone: 'UTC', timeZoneIsKnown: true, locationCaptureEnabled: true, requirePeopleProposalApproval: false, version: 2 }
  await commands.answerRequests([{ method: 'GET', path: '/api/v1/me/settings', body: row }])
  const queryClient = new QueryClient()
  queryClient.setQueryData(SettingKeys.mine(), row)
  expect(locationCaptureIsOn(queryClient)).toBe(true)
  const saver = settingSaver(queryClient)
  const readsBefore = rowReads()

  saver.adopt({ change: null, attempt: null, asks: [{ field: 'locationCaptureEnabled', yours: false, now: true }] })
  expect(locationCaptureIsOn(queryClient)).toBe(false)
  expect(locationWaitsForAnAnswer(queryClient)).toBe(true)
  await vi.waitFor(() => expect(rowReads()).toBe(readsBefore + 1))
  queryClient.setQueryData(SettingKeys.mine(), { ...row, locationCaptureEnabled: false })
  expect(locationWaitsForAnAnswer(queryClient)).toBe(false)
  queryClient.setQueryData(SettingKeys.mine(), row)

  saver.answerAsk(saver.asks()[0], false)
  expect(locationCaptureIsOn(queryClient)).toBe(true)
  expect(locationWaitsForAnAnswer(queryClient)).toBe(false)
})

test('the row stays known when no page reads it, so a create that retries much later still sends the location', async () => {
  const row = { timeZone: 'UTC', timeZoneIsKnown: true, locationCaptureEnabled: true, requirePeopleProposalApproval: false, version: 2 }
  await commands.answerRequests([{ method: 'GET', path: '/api/v1/me/settings', body: row }])
  vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] })
  const queryClient = new QueryClient()

  await queryClient.fetchQuery(userSettingOptions)
  vi.advanceTimersByTime(6 * 60_000)
  vi.useRealTimers()

  expect(locationCaptureIsOn(queryClient)).toBe(true)
})

test('no location is saved while any answer about location waits, in either direction', async () => {
  const row = { timeZone: 'UTC', timeZoneIsKnown: true, locationCaptureEnabled: true, requirePeopleProposalApproval: false, version: 2 }
  await commands.answerRequests([{ method: 'GET', path: '/api/v1/me/settings', body: row }])
  const queryClient = new QueryClient()
  queryClient.setQueryData(SettingKeys.mine(), row)
  const saver = settingSaver(queryClient)
  const readsBefore = rowReads()

  saver.adopt({ change: null, attempt: null, asks: [{ field: 'requirePeopleProposalApproval', yours: true, now: false }] })
  expect(locationCaptureIsOn(queryClient)).toBe(true)

  saver.adopt({ change: null, attempt: null, asks: [{ field: 'locationCaptureEnabled', yours: false, now: true }] })
  expect(locationCaptureIsOn(queryClient)).toBe(false)
  // The second read starts while the first runs, so the query client joins them in one request.
  await vi.waitFor(() => expect(rowReads()).toBe(readsBefore + 1))
})

const aRow = (patch: Partial<UserSettingDto>): UserSettingDto => ({ timeZoneMode: 'Fixed', timeZone: 'UTC', timeZoneIsKnown: true, locationCaptureEnabled: false, requirePeopleProposalApproval: false, requireImportReview: true, removeFillerWords: true, writeNumbersAsDigits: true, version: 1, ...patch })

const captureZoneWith = (row: UserSettingDto) => {
  const queryClient = new QueryClient()
  queryClient.setQueryData(SettingKeys.mine(), row)
  return captureZoneOf(queryClient)
}

test('a new entry that starts before the row loads sends the zone of this browser', () => {
  expect(captureZoneOf(new QueryClient())).toBe(Intl.DateTimeFormat().resolvedOptions().timeZone)
})

test('a new entry sends the fixed zone of the row, also when this browser does not know the zone', () => {
  expect(captureZoneWith(aRow({ timeZoneMode: 'Fixed', timeZone: 'Mars/Olympus_Mons' }))).toBe('Mars/Olympus_Mons')
})

test('a new entry sends no zone while the setting follows a browser that cannot name its zone, so the Server takes the zone of the row', () => {
  const resolvedOptions = Intl.DateTimeFormat.prototype.resolvedOptions
  vi.spyOn(Intl.DateTimeFormat.prototype, 'resolvedOptions').mockImplementation(function (this: Intl.DateTimeFormat) {
    return Object.assign(resolvedOptions.call(this), { timeZone: undefined })
  })
  resetLocalTimeZone()
  try {
    expect(captureZoneWith(aRow({ timeZoneMode: 'FollowDevice', timeZone: 'Asia/Tokyo' }))).toBeNull()
  } finally {
    vi.restoreAllMocks()
    resetLocalTimeZone()
  }
})
