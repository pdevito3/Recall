import { QueryClient } from '@tanstack/react-query'
import { afterEach, beforeEach, expect, test, vi } from 'vitest'
import { commands } from 'vitest/browser'

import { resumeUnsentSetting } from './user-setting'

const prefix = 'recall.unsent-setting.person-a.'
const staleKey = {
  change: { value: { locationCaptureEnabled: true }, savedAt: '2026-11-06T00:00:00.000Z' },
  attempt: { base: { values: { locationCaptureEnabled: true, timeZone: 'UTC', requirePeopleProposalApproval: false }, version: 1 }, hadNoReply: false },
  asks: [],
}

let otherTab: BroadcastChannel

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
