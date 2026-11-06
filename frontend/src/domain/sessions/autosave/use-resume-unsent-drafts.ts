import { useQueryClient } from '@tanstack/react-query'
import { useEffect } from 'react'

import { resumeUnsentSetting } from '@/domain/settings/apis/user-setting'

import { resumeUnsentDrafts } from './draft-saver'
import { useOwner } from './use-draft-saver'

export function useResumeUnsentDrafts() {
  const owner = useOwner()
  const queryClient = useQueryClient()
  useEffect(() => {
    if (!owner) return
    resumeUnsentDrafts(owner, queryClient)
    resumeUnsentSetting(owner, queryClient)
  }, [owner, queryClient])
}
