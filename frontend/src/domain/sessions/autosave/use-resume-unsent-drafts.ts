import { useQueryClient } from '@tanstack/react-query'
import { useEffect } from 'react'

import { resumeUnsentSetting } from '@/domain/settings/apis/user-setting'

import { isAnyWordOnlyInMemory, resumeUnsentDrafts, settleDraftSavers, wordsOfThisTabOnlyInMemory } from './draft-saver'
import { joinTheOtherTabs, noteThisTabIsBack, noteThisTabLeaves, removeLeftMarksWithNoKey, watchOtherTabs } from './tab-keys'
import { isAnyTagChangeUnsaved } from './tag-savers'
import { useOwner } from './use-draft-saver'

const settleLimitForAnotherTabMs = 4_000

async function settleForASignOutInAnotherTab() {
  await Promise.race([settleDraftSavers(), new Promise((resolve) => setTimeout(resolve, settleLimitForAnotherTabMs))])
  return { wordsOnlyInMemory: wordsOfThisTabOnlyInMemory(), hasUnsavedTags: isAnyTagChangeUnsaved() }
}

export function useResumeUnsentDrafts() {
  const owner = useOwner()
  const queryClient = useQueryClient()
  useEffect(() => {
    if (!owner) return
    const resume = () => {
      void resumeUnsentDrafts(owner, queryClient)
      void resumeUnsentSetting(owner, queryClient)
      removeLeftMarksWithNoKey()
    }
    const resumeWhenSeen = () => {
      if (document.visibilityState === 'visible') resume()
    }
    resume()
    const stopWatching = watchOtherTabs(resume)
    document.addEventListener('visibilitychange', resumeWhenSeen)
    window.addEventListener('online', resume)
    return () => {
      stopWatching()
      document.removeEventListener('visibilitychange', resumeWhenSeen)
      window.removeEventListener('online', resume)
    }
  }, [owner, queryClient])

  useEffect(() => {
    joinTheOtherTabs(settleForASignOutInAnotherTab)
    const noteBack = ({ persisted }: PageTransitionEvent) => {
      if (persisted) noteThisTabIsBack()
    }
    window.addEventListener('pagehide', noteThisTabLeaves)
    window.addEventListener('pageshow', noteBack)
    return () => {
      window.removeEventListener('pagehide', noteThisTabLeaves)
      window.removeEventListener('pageshow', noteBack)
    }
  }, [])

  useEffect(() => {
    const warnOfTheLoss = (event: BeforeUnloadEvent) => {
      if (isAnyWordOnlyInMemory() || isAnyTagChangeUnsaved()) event.preventDefault()
    }
    window.addEventListener('beforeunload', warnOfTheLoss)
    return () => window.removeEventListener('beforeunload', warnOfTheLoss)
  }, [])
}
