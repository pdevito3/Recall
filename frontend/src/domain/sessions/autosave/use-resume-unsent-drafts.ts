import { useQueryClient } from '@tanstack/react-query'
import { useEffect } from 'react'

import { resumeUnsentDrafts } from './draft-saver'
import { useOwner } from './use-draft-saver'

export function useResumeUnsentDrafts() {
  const owner = useOwner()
  const queryClient = useQueryClient()
  useEffect(() => {
    if (owner) resumeUnsentDrafts(owner, queryClient)
  }, [owner, queryClient])
}
