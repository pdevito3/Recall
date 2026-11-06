import { useQueryClient } from '@tanstack/react-query'
import { useEffect } from 'react'

import { browserTimeZone } from '@/lib/zoned-time'

import { settingSaver, useUserSetting } from './user-setting'

let hasSeeded = false

export function useSeedTimeZone() {
  const queryClient = useQueryClient()
  const setting = useUserSetting().data
  useEffect(() => {
    if (!setting || setting.timeZone || hasSeeded) return
    hasSeeded = true
    settingSaver(queryClient).edit({
      timeZone: browserTimeZone(),
      locationCaptureEnabled: setting.locationCaptureEnabled,
      requirePeopleProposalApproval: setting.requirePeopleProposalApproval,
    })
  }, [setting, queryClient])
}
