import { useQuery, useQueryClient } from '@tanstack/react-query'
import { useSyncExternalStore } from 'react'

import { authUserQuery } from '@/domain/auth/apis/get-user'

import { draftSaverFor, type DraftSaver, type DraftSnapshot, type DraftTarget } from './draft-saver'

export function useOwner() {
  return useQuery(authUserQuery).data?.sub ?? ''
}

export function useDraftSaver(target: Omit<DraftTarget, 'owner'>): { saver: DraftSaver; snapshot: DraftSnapshot } {
  const owner = useOwner()
  const queryClient = useQueryClient()
  const saver = draftSaverFor({ ...target, owner }, queryClient)
  const snapshot = useSyncExternalStore(saver.subscribe, saver.getSnapshot)
  return { saver, snapshot }
}
