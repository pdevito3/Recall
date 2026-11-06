import type { QueryClient } from '@tanstack/react-query'
import { useSyncExternalStore } from 'react'

import { toastQueue } from '@/components/notification'
import { writePointer } from '@/domain/sessions/autosave/composer-pointer'
import { stopDraftSavers } from '@/domain/sessions/autosave/draft-saver'
import { stopSettingSaver } from '@/domain/settings/apis/user-setting'

export const signInForAnotherPersonPage = '/auth/login?anotherPerson=1'

let hasLeft = false
const listeners = new Set<() => void>()

const subscribe = (listener: () => void) => {
  listeners.add(listener)
  return () => {
    listeners.delete(listener)
  }
}

export const useHasLeftThePage = () => useSyncExternalStore(subscribe, () => hasLeft)

/**
 * Another person signed in on this browser, so nothing more of this page may reach the Server or stay on the screen.
 * The unsent words stay in the outbox under the key of the person who wrote them, and they go out at the next sign-in of that person.
 */
export function leaveThePageOfAnotherPerson(queryClient: QueryClient) {
  if (hasLeft) return
  hasLeft = true
  stopDraftSavers()
  stopSettingSaver()
  writePointer(null)
  void queryClient.cancelQueries()
  queryClient.clear()
  toastQueue.clear()
  for (const listener of listeners) listener()
  window.location.replace(signInForAnotherPersonPage)
}
