import { queryOptions, useQuery, type QueryClient } from '@tanstack/react-query'

import { userSettingDtoTimeZoneModeEnum, type UserSettingDto } from '@/api/generated/models/types/user-setting-dto'
import type { SessionForCreationDto } from '@/api/generated/models/types/session-for-creation-dto'
import { changeUserSetting } from '@/api/generated/user-settings/clients/change-user-setting'
import { getUserSetting } from '@/api/generated/user-settings/clients/get-user-setting'
import { refreshTimelineHead } from '@/domain/sessions/apis/timeline-cache'
import { Notification } from '@/components/notification'
import { RowSaver, type RowSaverState } from '@/domain/sessions/autosave/row-saver'
import { keysUnder, removeKey, takeKeysOfTabsThatLeft, writeKeyOfThisTab, type KeyOfATab } from '@/domain/sessions/autosave/tab-keys'
import { pageUserOf } from '@/lib/page-user'
import { retryUnlessRefused } from '@/lib/problem-details'
import { browserTimeZone, parseTimeZone, type TimeZone } from '@/lib/zoned-time'

import { SettingKeys } from './setting.keys'

export const userSettingOptions = queryOptions({
  queryKey: SettingKeys.mine(),
  queryFn: ({ signal }) => getUserSetting({ signal, options: { suppressErrorToast: true } }),
  staleTime: 60_000,
  // A retry of a create can come long after the last page that reads the row, and with no row it would drop the location.
  gcTime: Infinity,
  retry: retryUnlessRefused,
})

export function useUserSetting() {
  return useQuery(userSettingOptions)
}

export const followsTheDevice = (setting: Pick<UserSettingDto, 'timeZoneMode'>) => setting.timeZoneMode === userSettingDtoTimeZoneModeEnum.FollowDevice

/** The zone of the day on this device: the zone of this browser while the setting follows the device, or the zone that the person picked. */
export function journalZoneOf(setting: UserSettingDto | undefined): TimeZone {
  if (setting && followsTheDevice(setting)) return browserTimeZone()
  try {
    return parseTimeZone(setting?.timeZone ?? 'UTC')
  } catch {
    return 'UTC' as TimeZone
  }
}

export function useJournalZone(): TimeZone {
  return journalZoneOf(useUserSetting().data)
}

export function captureZoneOf(queryClient: QueryClient): SessionForCreationDto['timeZone'] {
  const setting = queryClient.getQueryData<UserSettingDto>(SettingKeys.mine())
  return setting && journalZoneOf(setting)
}

export type SettingChange = Pick<UserSettingDto, 'timeZoneMode' | 'timeZone' | 'locationCaptureEnabled' | 'requirePeopleProposalApproval'>

let saver: RowSaver<SettingChange, UserSettingDto> | undefined

let ownKey: string | null = null

const prefixOf = (owner: string) => `recall.unsent-setting.${encodeURIComponent(owner).replaceAll('.', '%2E')}.`

const settingFieldsOf = ({ timeZoneMode, timeZone, locationCaptureEnabled, requirePeopleProposalApproval }: SettingChange): SettingChange => ({
  timeZoneMode,
  timeZone,
  locationCaptureEnabled,
  requirePeopleProposalApproval,
})

function stateIn({ stored }: KeyOfATab): RowSaverState<SettingChange> | null {
  try {
    const state = JSON.parse(stored ?? 'null') as RowSaverState<SettingChange> | null
    return Array.isArray(state?.asks) ? state : null
  } catch {
    return null
  }
}

function keepUnsentSetting(owner: string, state: RowSaverState<SettingChange> | null) {
  if (state) ownKey = writeKeyOfThisTab(prefixOf(owner), ownKey, JSON.stringify(state)) ?? ownKey
  else if (ownKey !== null) {
    removeKey(ownKey)
    ownKey = null
  }
}

const savedAtOf = (state: RowSaverState<SettingChange> | null) => state?.change?.savedAt ?? ''

export const resumeUnsentSetting = (owner: string, queryClient: QueryClient) =>
  takeKeysOfTabsThatLeft(prefixOf(owner), (keys) => {
    const inOrder = [...keys].sort((one, other) => savedAtOf(stateIn(one)).localeCompare(savedAtOf(stateIn(other))))
    for (const key of inOrder) {
      const state = stateIn(key)
      if (state) settingSaver(queryClient).adopt(state)
      removeKey(key.storageKey)
    }
  })

const asksAboutLocation = ({ asks }: Pick<RowSaverState<SettingChange>, 'asks'>) =>
  asks.some(({ field }) => field === 'locationCaptureEnabled')

function waitingChanges(queryClient: QueryClient) {
  const saver = settingSaver(queryClient)
  const owner = pageUserOf()
  const inKeys = owner === null ? [] : keysUnder(prefixOf(owner)).flatMap((key) => stateIn(key) ?? [])
  return [...inKeys.map(({ change, asks }) => ({ fields: change?.value, asks })), { fields: saver.pendingFields(), asks: saver.asks() }]
}

const isOnInTheRow = (queryClient: QueryClient) => queryClient.getQueryData<UserSettingDto>(SettingKeys.mine())?.locationCaptureEnabled === true

/** A change of location can wait for a save or an answer, in this tab or in a key of another tab. The app saves no location while it waits, so the choice holds also on a page with no prompt. */
export const locationCaptureIsOn = (queryClient: QueryClient) =>
  isOnInTheRow(queryClient) && !waitingChanges(queryClient).some((waiting) => waiting.fields?.locationCaptureEnabled !== undefined || asksAboutLocation(waiting))

export const locationWaitsForAnAnswer = (queryClient: QueryClient) => isOnInTheRow(queryClient) && waitingChanges(queryClient).some(asksAboutLocation)

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
      sameChoice: (field, before, fresh) => field === 'timeZone' && followsTheDevice(before) && followsTheDevice(fresh),
      onlyFromALiveClick: (field, value) => field === 'locationCaptureEnabled' && value === true,
    },
    onAppliedWithNoAsk: () => Notification.info('Your location is no longer saved with new entries. Your settings changed elsewhere first, and this change still applied.'),
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
