import { useMutation, useQueryClient } from '@tanstack/react-query'

import type { UserDto } from '@/api/generated/models/types/user-dto'
import { addUserRole } from '@/api/generated/users/clients/add-user-role'
import { removeUserRole } from '@/api/generated/users/clients/remove-user-role'
import { refreshCallerMembership } from '@/domain/membership/apis/refresh-caller-membership'
import type { UserRole } from '@/domain/users/components/user-role'

import { UserKeys } from './user.keys'

function useRoleChange(id: string, send: (role: UserRole) => Promise<UserDto>) {
  const queryClient = useQueryClient()

  return useMutation({
    mutationFn: send,
    onMutate: () => queryClient.cancelQueries({ queryKey: UserKeys.detail(id) }),
    onSuccess: (user) => {
      queryClient.setQueryData(UserKeys.detail(id), user)
      return Promise.all([
        queryClient.invalidateQueries({ queryKey: UserKeys.lists() }),
        refreshCallerMembership(queryClient),
      ])
    },
    onError: () =>
      Promise.all([
        queryClient.invalidateQueries({ queryKey: UserKeys.detail(id) }),
        queryClient.invalidateQueries({ queryKey: UserKeys.lists() }),
      ]),
  })
}

export const useAddUserRole = (id: string) => useRoleChange(id, (role) => addUserRole({ path: { id }, body: { role } }))

export const useRemoveUserRole = (id: string) => useRoleChange(id, (role) => removeUserRole({ path: { id, role } }))
