import { useQueryClient } from '@tanstack/react-query'
import { useEffect } from 'react'

import { useOwner } from '@/domain/sessions/autosave/use-draft-saver'

import { sendStoredAudioOf } from './upload-queue'

/** Sends the audio that waits in this browser from each page of the app, so a reload on any page sends what a closed tab left. */
export function useSendStoredAudio() {
  const owner = useOwner()
  const queryClient = useQueryClient()
  useEffect(() => {
    if (!owner || typeof indexedDB === 'undefined') return
    return sendStoredAudioOf(owner, queryClient)
  }, [owner, queryClient])
}
