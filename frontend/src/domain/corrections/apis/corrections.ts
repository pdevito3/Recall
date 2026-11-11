import { queryOptions, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'

import { addCorrection } from '@/api/generated/corrections/clients/add-correction'
import { deleteCorrection } from '@/api/generated/corrections/clients/delete-correction'
import { getCorrection } from '@/api/generated/corrections/clients/get-correction'
import { getCorrectionList } from '@/api/generated/corrections/clients/get-correction-list'
import { updateCorrection } from '@/api/generated/corrections/clients/update-correction'
import type { CorrectionDto } from '@/api/generated/models/types/correction-dto'
import type { CorrectionForCreationDto } from '@/api/generated/models/types/correction-for-creation-dto'
import type { CorrectionForUpdateDto } from '@/api/generated/models/types/correction-for-update-dto'
import { parsePagination } from '@/lib/pagination'
import { retryUnlessRefused } from '@/lib/problem-details'

import { CorrectionKeys } from './correction.keys'

const directoryPageSize = 100

const termOrder = new Intl.Collator(undefined, { numeric: true })

async function readDirectory(signal: AbortSignal): Promise<CorrectionDto[]> {
  const corrections: CorrectionDto[] = []
  for (let pageNumber = 1; ; pageNumber++) {
    const result = await getCorrectionList({
      query: { pageNumber, pageSize: directoryPageSize },
      signal,
      options: { suppressErrorToast: true },
    })
    corrections.push(...result.data)
    if (!parsePagination(result).hasNext)
      return corrections.sort((one, other) => termOrder.compare(one.canonicalTerm, other.canonicalTerm) || (one.id < other.id ? -1 : 1))
  }
}

export function useCorrectionDirectory() {
  return useQuery({
    queryKey: CorrectionKeys.directory(),
    queryFn: ({ signal }) => readDirectory(signal),
    retry: retryUnlessRefused,
  })
}

export function correctionOptions(id: string) {
  return queryOptions({
    queryKey: CorrectionKeys.detail(id),
    queryFn: ({ signal }) => getCorrection({ path: { id }, signal }),
    retry: retryUnlessRefused,
  })
}

export function useAddCorrection() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (body: CorrectionForCreationDto) => addCorrection({ body, options: { suppressErrorToast: true } }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: CorrectionKeys.directory() }),
  })
}

export function useUpdateCorrection() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({ id, body }: { id: string; body: CorrectionForUpdateDto }) =>
      updateCorrection({ path: { id }, body, options: { suppressErrorToast: true } }),
    onMutate: ({ id }) => queryClient.cancelQueries({ queryKey: CorrectionKeys.detail(id) }),
    onSuccess: (saved) => {
      queryClient.setQueryData(CorrectionKeys.detail(saved.id), saved)
      return queryClient.invalidateQueries({ queryKey: CorrectionKeys.directory() })
    },
  })
}

export function useDeleteCorrection() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (id: string) => deleteCorrection({ path: { id } }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: CorrectionKeys.directory() }),
  })
}
