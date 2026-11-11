import { onlineManager, QueryClient, QueryClientProvider } from '@tanstack/react-query'
import type { ReactNode } from 'react'
import { expect, onTestFinished, test, vi } from 'vitest'
import { commands } from 'vitest/browser'
import { renderHook } from 'vitest-browser-react'

import { Notification } from '@/components/notification'

import { useLogout } from './auth-actions'
import { settleUnsentWords } from './unsent-words-at-logout'

vi.mock('./unsent-words-at-logout', async (original) => ({
  ...(await original<typeof import('./unsent-words-at-logout')>()),
  settleUnsentWords: vi.fn(),
}))

const wrapper = ({ children }: { children: ReactNode }) => <QueryClientProvider client={new QueryClient()}>{children}</QueryClientProvider>

test('a sign-out with no network keeps what the person chose to discard, and says that a sign-out needs the network', async () => {
  // Arrange
  const leave = vi.fn(() => Promise.resolve())
  vi.mocked(settleUnsentWords).mockResolvedValue(leave)
  const error = vi.spyOn(Notification, 'error')
  await commands.answerRequests([{ method: 'POST', path: '/auth/logout', status: 503, body: {} }])
  onlineManager.setOnline(false)
  onTestFinished(() => onlineManager.setOnline(true))
  const { result } = await renderHook(() => useLogout(), { wrapper })

  // Act
  result.current.mutate()

  // Assert
  await expect.poll(() => error.mock.calls.map(([title]) => title)).toEqual(['You are still signed in, because a sign-out needs the network. Try again when you are online.'])
  expect(leave).not.toHaveBeenCalled()
})
