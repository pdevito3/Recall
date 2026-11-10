import { useQuery } from '@tanstack/react-query'

import { getFileStoreStatus } from '@/api/generated/file-store/clients/get-file-store-status'
import { retryUnlessRefused } from '@/lib/problem-details'

import { FileStoreKeys } from './file-store.keys'

export function useFileStoreStatus() {
  return useQuery({
    queryKey: FileStoreKeys.status(),
    queryFn: ({ signal }) => getFileStoreStatus({ signal, options: { suppressErrorToast: true } }),
    staleTime: 30_000,
    refetchInterval: 60_000,
    retry: retryUnlessRefused,
  })
}
