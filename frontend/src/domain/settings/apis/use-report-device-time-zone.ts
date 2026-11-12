import { useQueryClient } from '@tanstack/react-query'
import { useEffect } from 'react'

import { reportDeviceTimeZone } from '@/api/generated/user-settings/clients/report-device-time-zone'
import { deviceTimeZone } from '@/lib/zoned-time'

import { SettingKeys } from './setting.keys'
import { followsTheDevice, useUserSetting } from './user-setting'

const reportedZones = new Set<string>()

export function useReportDeviceTimeZone() {
  const queryClient = useQueryClient()
  const setting = useUserSetting().data
  useEffect(() => {
    if (!setting) return
    if (!followsTheDevice(setting)) {
      // A later pick of Automatic saves no zone, so the row keeps this zone until a report.
      reportedZones.clear()
      return
    }
    const zone = deviceTimeZone()
    if (zone === null) return
    if (setting.timeZone === zone || reportedZones.has(zone)) return
    reportedZones.add(zone)
    void reportDeviceTimeZone({ body: { timeZone: zone }, options: { suppressErrorToast: true } })
      .then((reply) => queryClient.setQueryData(SettingKeys.mine(), reply))
      .catch(() => reportedZones.delete(zone))
  }, [setting, queryClient])
}
