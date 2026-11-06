import type { QueryClient } from '@tanstack/react-query'

import type { SessionDto } from '@/api/generated/models/types/session-dto'
import { setSessionMetadata } from '@/api/generated/sessions/clients/set-session-metadata'

import { SessionKeys } from '../apis/session.keys'
import { patchTimelineRow } from '../apis/timeline-cache'
import type { SessionTagValues } from '../tags'
import { isARowThatIsGone, SerialSaver } from './serial-saver'

const savers = new Map<string, SerialSaver<SessionTagValues, SessionDto>>()

export function metadataSaverFor(id: string, queryClient: QueryClient) {
  const existing = savers.get(id)
  if (existing) return existing
  const saver = new SerialSaver<SessionTagValues, SessionDto>({
    debounceMs: 0,
    send: ({ value, savedAt }, signal) =>
      setSessionMetadata({ path: { id }, body: { ...value, savedAt }, signal, options: { suppressErrorToast: true } }),
    refusalMessage: (error) => (isARowThatIsGone(error) ? 'This entry was deleted in another tab or device.' : error.displayMessage),
    onReply: (session) => {
      void queryClient.cancelQueries({ queryKey: SessionKeys.detail(id), exact: true })
      queryClient.setQueryData(SessionKeys.detail(id), session)
      patchTimelineRow(queryClient, id, {
        activity: session.activity,
        moods: session.moods,
        topics: session.topics,
        version: session.version,
      })
    },
  })
  savers.set(id, saver)
  return saver
}

export function discardMetadataSaver(id: string) {
  savers.get(id)?.dispose()
  savers.delete(id)
}
