import { queryOptions, useQuery, type QueryClient } from '@tanstack/react-query'

import type { UserSettingDto } from '@/api/generated/models/types/user-setting-dto'
import { changeUserSetting } from '@/api/generated/user-settings/clients/change-user-setting'
import { getUserSetting } from '@/api/generated/user-settings/clients/get-user-setting'
import { refreshTimelineHead } from '@/domain/sessions/apis/timeline-cache'
import { newestUnsettled, queueWithUnanswered, type SaveQueue, type Stamped } from '@/domain/sessions/autosave/save-queue'
import { SerialSaver } from '@/domain/sessions/autosave/serial-saver'
import { pageUserOf } from '@/lib/page-user'
import { retryUnlessRefused } from '@/lib/problem-details'
import { parseTimeZone, type TimeZone } from '@/lib/zoned-time'

import { SettingKeys } from './setting.keys'

export const userSettingOptions = queryOptions({
  queryKey: SettingKeys.mine(),
  queryFn: ({ signal }) => getUserSetting({ signal, options: { suppressErrorToast: true } }),
  staleTime: 60_000,
  retry: retryUnlessRefused,
})

export function useUserSetting() {
  return useQuery(userSettingOptions)
}

export function journalZoneOf(setting: UserSettingDto | undefined): TimeZone {
  try {
    return parseTimeZone(setting?.timeZone ?? 'UTC')
  } catch {
    return 'UTC' as TimeZone
  }
}

export function useJournalZone(): TimeZone {
  return journalZoneOf(useUserSetting().data)
}

export type SettingChange = Pick<UserSettingDto, 'timeZone' | 'locationCaptureEnabled' | 'requirePeopleProposalApproval'>

let saver: SerialSaver<SettingChange, UserSettingDto> | undefined

const unsentKeyOf = (owner: string) => `recall.unsent-setting.${owner}`

function readUnsentSetting(owner: string): Stamped<SettingChange> | null {
  try {
    const stored = JSON.parse(localStorage.getItem(unsentKeyOf(owner)) ?? 'null') as Stamped<SettingChange> | null
    return typeof stored?.savedAt === 'string' && typeof stored.value.locationCaptureEnabled === 'boolean' ? stored : null
  } catch {
    return null
  }
}

function keepUnsentSetting(owner: string, queue: SaveQueue<SettingChange>) {
  const change = queue.refusal ? null : newestUnsettled(queue)
  try {
    if (change) localStorage.setItem(unsentKeyOf(owner), JSON.stringify(change))
    else localStorage.removeItem(unsentKeyOf(owner))
  } catch {
    return
  }
}

export function resumeUnsentSetting(owner: string, queryClient: QueryClient) {
  if (readUnsentSetting(owner)) settingSaver(queryClient)
}

export const settleSettingSaver = () => saver?.settle() ?? Promise.resolve(true)

export function stopSettingSaver() {
  saver?.dispose()
}

export function settingSaver(queryClient: QueryClient) {
  const owner = pageUserOf()
  const unsent = owner ? readUnsentSetting(owner) : null
  saver ??= new SerialSaver<SettingChange, UserSettingDto>({
    debounceMs: 0,
    initialQueue: unsent ? queueWithUnanswered(unsent) : undefined,
    onQueueChange: (queue) => {
      if (owner) keepUnsentSetting(owner, queue)
    },
    send: ({ value, savedAt }, signal) =>
      changeUserSetting({ body: { ...value, savedAt }, signal, options: { suppressErrorToast: true } }),
    onReply: (reply) => {
      const zoneChanged = queryClient.getQueryData<UserSettingDto>(SettingKeys.mine())?.timeZone !== reply.timeZone
      queryClient.setQueryData(SettingKeys.mine(), reply)
      if (zoneChanged) void refreshTimelineHead(queryClient)
    },
  })
  return saver
}
