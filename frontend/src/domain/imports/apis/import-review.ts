import { useMutation, useQueryClient } from '@tanstack/react-query'

import type { UserSettingDto } from '@/api/generated/models/types/user-setting-dto'
import { changeImportReview } from '@/api/generated/user-settings/clients/change-import-review'
import { SettingKeys } from '@/domain/settings/apis/setting.keys'
import { userSettingOptions } from '@/domain/settings/apis/user-setting'
import { isStaleVersionError } from '@/lib/problem-details'

export function useChangeImportReview() {
  const queryClient = useQueryClient()
  const send = (requireImportReview: boolean, version: number) =>
    changeImportReview({ body: { requireImportReview, version }, options: { suppressErrorToast: true } })

  return useMutation({
    mutationFn: async (requireImportReview: boolean) => {
      const read = queryClient.getQueryData<UserSettingDto>(SettingKeys.mine()) ?? (await queryClient.fetchQuery(userSettingOptions))
      try {
        return await send(requireImportReview, read.version)
      } catch (error) {
        if (!isStaleVersionError(error)) throw error
        const fresh = await queryClient.fetchQuery({ ...userSettingOptions, staleTime: 0 })
        return send(requireImportReview, fresh.version)
      }
    },
    onSuccess: async (setting) => {
      // A read that started before this save holds the old setting, and it must not answer over this reply.
      await queryClient.cancelQueries({ queryKey: SettingKeys.mine(), exact: true })
      queryClient.setQueryData(SettingKeys.mine(), setting)
    },
  })
}
