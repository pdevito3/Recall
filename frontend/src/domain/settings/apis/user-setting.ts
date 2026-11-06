import { queryOptions, useQuery, type QueryClient } from '@tanstack/react-query'

import type { UserSettingDto } from '@/api/generated/models/types/user-setting-dto'
import { changeUserSetting } from '@/api/generated/user-settings/clients/change-user-setting'
import { getUserSetting } from '@/api/generated/user-settings/clients/get-user-setting'
import { SerialSaver } from '@/domain/sessions/autosave/serial-saver'
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

/** The zone of the journal. A User with no zone, or a zone that this browser does not know, uses UTC, as the Server does. */
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

/** One saver for the one settings row, so its changes go one at a time, in order. */
export function settingSaver(queryClient: QueryClient) {
  saver ??= new SerialSaver<SettingChange, UserSettingDto>({
    debounceMs: 0,
    send: ({ value, savedAt }) =>
      changeUserSetting({ body: { ...value, savedAt }, options: { suppressErrorToast: true } }),
    onReply: (reply) => queryClient.setQueryData(SettingKeys.mine(), reply),
  })
  return saver
}
