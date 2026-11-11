import type { QueryClient } from '@tanstack/react-query'

import { toastQueue } from '@/components/notification'
import { writePointer } from '@/domain/sessions/autosave/composer-pointer'

import { leaveTheApp } from './leave-the-app'

export function leaveThePageOfAnotherPerson(queryClient: QueryClient) {
  leaveTheApp({ why: 'anotherPerson' })
  writePointer(null)
  void queryClient.cancelQueries()
  queryClient.clear()
  toastQueue.clear()
}
