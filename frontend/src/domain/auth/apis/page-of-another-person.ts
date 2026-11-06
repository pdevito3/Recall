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
