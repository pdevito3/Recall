import { queryOptions, useQuery, type QueryClient } from '@tanstack/react-query'

import type { UserSettingDto } from '@/api/generated/models/types/user-setting-dto'
import { changeUserSetting } from '@/api/generated/user-settings/clients/change-user-setting'
import { getUserSetting } from '@/api/generated/user-settings/clients/get-user-setting'
import { refreshTimelineHead } from '@/domain/sessions/apis/timeline-cache'
import { Notification } from '@/components/notification'
import { RowSaver, type RowSaverState } from '@/domain/sessions/autosave/row-saver'
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

let saver: RowSaver<SettingChange, UserSettingDto> | undefined

const unsentKeyOf = (owner: string) => `recall.unsent-setting.${owner}`

const settingFieldsOf = ({ timeZone, locationCaptureEnabled, requirePeopleProposalApproval }: SettingChange): SettingChange => ({
  timeZone,
  locationCaptureEnabled,
  requirePeopleProposalApproval,
})

function readUnsentSetting(owner: string): RowSaverState<SettingChange> | null {
  try {
    const stored = JSON.parse(localStorage.getItem(unsentKeyOf(owner)) ?? 'null') as RowSaverState<SettingChange> | null
    return Array.isArray(stored?.asks) ? stored : null
  } catch {
    return null
  }
}

function keepUnsentSetting(owner: string, state: RowSaverState<SettingChange> | null) {
  try {
    if (state) localStorage.setItem(unsentKeyOf(owner), JSON.stringify(state))
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
  saver ??= new RowSaver<SettingChange, UserSettingDto>({
    copyOf: () => queryClient.getQueryData<UserSettingDto>(SettingKeys.mine()),
    read: () => queryClient.fetchQuery({ ...userSettingOptions, staleTime: 0 }),
    fieldsOf: settingFieldsOf,
    versionOf: (setting) => setting.version,
    put: (values, version, signal) => changeUserSetting({ body: { ...values, version }, signal, options: { suppressErrorToast: true } }),
    rules: {
      asksFirst: (field, value) => field === 'locationCaptureEnabled' && value === true,
      neverAsks: (field, value) => field === 'locationCaptureEnabled' && value === false,
      // The first save of a zone comes from the browser. A zone on the row is a choice of the person.
      yieldsToTheRow: (field, before) => field === 'timeZone' && !before,
    },
    onAppliedWithNoAsk: () => Notification.info('Your location is no longer saved with new entries. Your settings changed elsewhere first, and this change still applied.'),
    resumed: owner ? readUnsentSetting(owner) : null,
    onStateChange: (state) => {
      if (owner) keepUnsentSetting(owner, state)
    },
    onReply: (reply) => {
      const zoneChanged = queryClient.getQueryData<UserSettingDto>(SettingKeys.mine())?.timeZone !== reply.timeZone
      // A read that started before this save holds the old setting, and it must not answer over this reply.
      void queryClient.cancelQueries({ queryKey: SettingKeys.mine(), exact: true })
      queryClient.setQueryData(SettingKeys.mine(), reply)
      if (zoneChanged) void refreshTimelineHead(queryClient)
    },
  })
  return saver
}
