import { useQueryClient, type QueryClient } from '@tanstack/react-query'
import { useEffect } from 'react'

import type { UserSettingDto } from '@/api/generated/models/types/user-setting-dto'
import { reportDeviceTimeZone } from '@/api/generated/user-settings/clients/report-device-time-zone'
import { deviceTimeZone, type TimeZone } from '@/lib/zoned-time'

import { SettingKeys } from './setting.keys'
import { followsTheDevice, useUserSetting } from './user-setting'

let reportedZone: TimeZone | null = null

const reportAgainNextTime = () => {
  reportedZone = null
}

const newerSettingOf = (cached: UserSettingDto | undefined, reply: UserSettingDto) => (cached && cached.version > reply.version ? cached : reply)

function reportTheZone(queryClient: QueryClient) {
  const setting = queryClient.getQueryData<UserSettingDto>(SettingKeys.mine())
  if (!setting) return
  if (!followsTheDevice(setting)) {
    reportAgainNextTime()
    return
  }
  const zone = deviceTimeZone()
  if (zone === null || zone === setting.timeZone || zone === reportedZone) return
  reportedZone = zone
  void reportDeviceTimeZone({ body: { timeZone: zone }, options: { suppressErrorToast: true } })
    .then((reply) => queryClient.setQueryData<UserSettingDto>(SettingKeys.mine(), (cached) => newerSettingOf(cached, reply)))
    .catch(() => {
      if (reportedZone === zone) reportAgainNextTime()
    })
}

export function useReportDeviceTimeZone() {
  const queryClient = useQueryClient()
  const setting = useUserSetting().data
  useEffect(() => reportTheZone(queryClient), [setting, queryClient])
  useEffect(() => {
    const reportAgainOnReturn = () => {
      if (document.visibilityState !== 'visible') return
      reportAgainNextTime()
      reportTheZone(queryClient)
    }
    document.addEventListener('visibilitychange', reportAgainOnReturn)
    window.addEventListener('focus', reportAgainOnReturn)
    return () => {
      document.removeEventListener('visibilitychange', reportAgainOnReturn)
      window.removeEventListener('focus', reportAgainOnReturn)
    }
  }, [queryClient])
}
