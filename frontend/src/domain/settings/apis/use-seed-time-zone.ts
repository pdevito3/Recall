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
    const saver = settingSaver(queryClient)
    const values = saver.newestUnsettledValue() ?? setting
    saver.edit({
      timeZone: browserTimeZone(),
      locationCaptureEnabled: values.locationCaptureEnabled,
      requirePeopleProposalApproval: values.requirePeopleProposalApproval,
    })
  }, [setting, queryClient])
}
