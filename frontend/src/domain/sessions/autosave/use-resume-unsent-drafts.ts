import { useQueryClient } from '@tanstack/react-query'
import { useEffect } from 'react'

import { resumeUnsentSetting } from '@/domain/settings/apis/user-setting'

import { isAnyWordOnlyInMemory, resumeUnsentDrafts } from './draft-saver'
import { isAnyTagChangeUnsaved } from './tag-savers'
import { useOwner } from './use-draft-saver'

export function useResumeUnsentDrafts() {
  const owner = useOwner()
  const queryClient = useQueryClient()
  useEffect(() => {
    if (!owner) return
    resumeUnsentDrafts(owner, queryClient)
    resumeUnsentSetting(owner, queryClient)
  }, [owner, queryClient])

  useEffect(() => {
    const warnOfTheLoss = (event: BeforeUnloadEvent) => {
      if (isAnyWordOnlyInMemory() || isAnyTagChangeUnsaved()) event.preventDefault()
    }
    window.addEventListener('beforeunload', warnOfTheLoss)
    return () => window.removeEventListener('beforeunload', warnOfTheLoss)
  }, [])
}
