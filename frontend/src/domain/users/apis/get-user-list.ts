import { keepPreviousData, useQuery } from '@tanstack/react-query'

import { getUserList } from '@/api/generated/users/clients/get-user-list'
import { parsePagination } from '@/lib/pagination'
import { retryUnlessRefused } from '@/lib/problem-details'

import { UserKeys } from './user.keys'

export const USER_PAGE_SIZE = 10

export interface UserListParams {
  page: number
}

export function useUserList(params: UserListParams, { enabled = true }: { enabled?: boolean } = {}) {
  return useQuery({
    enabled,
    queryKey: UserKeys.list(params),
    queryFn: async ({ signal }) => {
      // The page shows the failure, so a toast would say it twice.
      const result = await getUserList({
        query: { pageNumber: params.page, pageSize: USER_PAGE_SIZE },
        signal,
        options: { suppressErrorToast: true },
      })
      return { users: result.data, pagination: parsePagination(result) }
    },
    placeholderData: keepPreviousData,
    retry: retryUnlessRefused,
  })
}
