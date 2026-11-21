import { useMutation, useQueryClient } from '@tanstack/react-query'

import type { UserDto } from '@/api/generated/models/types/user-dto'
import { issueTemporaryPassword } from '@/api/generated/users/clients/issue-temporary-password'

import { UserKeys } from './user.keys'

/**
 * The answer holds the temporary password. Only this mutation result keeps it. The dialog resets the mutation when it
 * closes, and gcTime 0 then drops it from the mutation cache. The issue ends each session of the user, also the session
 * of a caller who issues one to themselves, so the hook sends no request after it: it puts the login name in the detail
 * and marks the lists stale without a fetch.
 */
export function useIssueTemporaryPassword(id: string) {
  const queryClient = useQueryClient()
  return useMutation({
    gcTime: 0,
    mutationFn: (loginName: string | undefined) =>
      issueTemporaryPassword({ path: { id }, body: { loginName }, options: { suppressErrorToast: true } }),
    onSuccess: ({ loginName }) => {
      queryClient.setQueryData<UserDto>(UserKeys.detail(id), (user) => user && { ...user, loginName })
      return queryClient.invalidateQueries({ queryKey: UserKeys.lists(), refetchType: 'none' })
    },
  })
}
