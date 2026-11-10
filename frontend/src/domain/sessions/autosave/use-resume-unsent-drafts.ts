import { useQueryClient } from '@tanstack/react-query'
import { useEffect } from 'react'

import { stopRecordingOfThisTab } from '@/domain/capture/use-capture'
import { resumeUnsentSetting } from '@/domain/settings/apis/user-setting'

import { isAnyWordOnlyInMemory, refreshWordsOfEditors, resumeUnsentDrafts, settleDraftSavers, wordsOfThisTabOnlyInMemory } from './draft-saver'
import { prefixOfOutbox } from './outbox'
import { joinTheOtherTabs, noteThisTabIsBack, noteThisTabLeaves, removeLeftMarksWithNoKey, watchOtherTabs } from './tab-keys'
import { isAnyTagChangeUnsaved } from './tag-savers'
import { useOwner } from './use-draft-saver'

const settleLimitForAnotherTabMs = 4_000

async function settleForASignOutInAnotherTab() {
  // The recording stops first, so the tab that signs out sends its last slice and its end with the rest of the audio.
  await Promise.race([Promise.all([settleDraftSavers(), stopRecordingOfThisTab()]), new Promise((resolve) => setTimeout(resolve, settleLimitForAnotherTabMs))])
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
    // A page that shows an entry with no edit follows the words that the tab that edits keeps.
    const followTheEditor = ({ key }: StorageEvent) => {
      if (key === null || key.startsWith(prefixOfOutbox(owner))) refreshWordsOfEditors()
    }
    resume()
    const stopWatching = watchOtherTabs(resume)
    document.addEventListener('visibilitychange', resumeWhenSeen)
    window.addEventListener('online', resume)
    window.addEventListener('storage', followTheEditor)
    return () => {
      stopWatching()
      document.removeEventListener('visibilitychange', resumeWhenSeen)
      window.removeEventListener('online', resume)
      window.removeEventListener('storage', followTheEditor)
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
