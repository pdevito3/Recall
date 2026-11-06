import type { QueryClient } from '@tanstack/react-query'

import type { SessionDto } from '@/api/generated/models/types/session-dto'
import { setSessionMetadata } from '@/api/generated/sessions/clients/set-session-metadata'

import { sessionQueryOptions } from '../apis/get-session'
import { SessionKeys } from '../apis/session.keys'
import { patchTimelineRow } from '../apis/timeline-cache'
import { tagValuesOf, type SessionTagValues } from '../tags'
import { RowSaver } from './row-saver'
import { isARowThatIsGone } from './serial-saver'

const savers = new Map<string, RowSaver<SessionTagValues, SessionDto>>()

export function metadataSaverFor(id: string, queryClient: QueryClient) {
  const existing = savers.get(id)
  if (existing) return existing
  const saver = new RowSaver<SessionTagValues, SessionDto>({
    copyOf: () => queryClient.getQueryData<SessionDto>(SessionKeys.detail(id)),
    read: () => queryClient.fetchQuery({ ...sessionQueryOptions(id), staleTime: 0 }),
    fieldsOf: tagValuesOf,
    versionOf: (session) => session.metadataVersion,
    put: (values, metadataVersion, signal) =>
      setSessionMetadata({ path: { id }, body: { ...values, metadataVersion }, signal, options: { suppressErrorToast: true } }),
    refusalMessage: (error) => (isARowThatIsGone(error) ? 'This entry was deleted in another tab or device.' : error.displayMessage),
    onReply: (session) => {
      // A read that started before this save holds the old row, and it must not answer over this reply.
      void queryClient.cancelQueries({ queryKey: SessionKeys.detail(id), exact: true })
      queryClient.setQueryData(SessionKeys.detail(id), session)
      patchTimelineRow(queryClient, id, {
        activity: session.activity,
        moods: session.moods,
        topics: session.topics,
        version: session.version,
        metadataVersion: session.metadataVersion,
      })
    },
  })
  savers.set(id, saver)
  return saver
}

export const isAnyTagChangeUnsaved = () => [...savers.values()].some((saver) => saver.hasUnsaved())

export const stopMetadataSavers = () => {
  for (const saver of savers.values()) saver.dispose()
}

export const settleMetadataSavers = async () => (await Promise.all([...savers.values()].map((saver) => saver.settle()))).every(Boolean)

export function discardMetadataSaver(id: string) {
  savers.get(id)?.dispose()
  savers.delete(id)
}
