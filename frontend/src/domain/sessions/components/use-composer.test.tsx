import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import type { ReactNode } from 'react'
import { afterEach, expect, test, vi } from 'vitest'
import { renderHook } from 'vitest-browser-react'

import { addSession } from '@/api/generated/sessions/clients/add-session'
import { AuthKeys } from '@/domain/auth/apis/auth.keys'
import { SettingKeys } from '@/domain/settings/apis/setting.keys'
import { memberSession } from '@/test/session'

import { stopDraftSavers } from '../autosave/draft-saver'
import { useComposer } from './use-composer'

vi.mock('@/api/generated/sessions/clients/add-session')

afterEach(() => {
  stopDraftSavers()
  vi.resetAllMocks()
  localStorage.clear()
  sessionStorage.clear()
})

const removeEachKey = () => {
  for (const key of Object.keys(localStorage)) if (key.startsWith('recall.unsent')) localStorage.removeItem(key)
}

test('Done waits while a notice asks the person about the words, so the notice does not end with no answer', async () => {
  vi.mocked(addSession).mockRejectedValueOnce(new Error('Network Error'))
  vi.mocked(addSession).mockReturnValue(new Promise(() => undefined))
  const queryClient = new QueryClient({ defaultOptions: { queries: { staleTime: Infinity } } })
  queryClient.setQueryData(AuthKeys.user(), memberSession)
  queryClient.setQueryData(SettingKeys.mine(), { timeZone: 'UTC', timeZoneIsKnown: true, locationCaptureEnabled: false, requirePeopleProposalApproval: false, version: 1 })
  const wrapper = ({ children }: { children: ReactNode }) => <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
  const { result, act } = await renderHook(() => useComposer(), { wrapper })

  await act(() => result.current.handleChange('my words', false))
  await vi.waitFor(() => expect(addSession).toHaveBeenCalledOnce())
  removeEachKey()
  window.dispatchEvent(new Event('online'))

  await vi.waitFor(() => expect(result.current.conflict).toBe('discardedInAnotherTab'))
  expect(result.current.canFinish).toBe(false)
  await act(() => result.current.finish())
  expect(result.current.conflict).toBe('discardedInAnotherTab')
})
