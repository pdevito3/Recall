import { useIsMutating, useMutation, useQuery, useQueryClient, type QueryClient } from '@tanstack/react-query'

import { acceptImportBatch } from '@/api/generated/import-batches/clients/accept-import-batch'
import { discardImportBatch } from '@/api/generated/import-batches/clients/discard-import-batch'
import { getImportBatches } from '@/api/generated/import-batches/clients/get-import-batches'
import { getImportDays } from '@/api/generated/import-batches/clients/get-import-days'
import { getImportEntries } from '@/api/generated/import-batches/clients/get-import-entries'
import { reviewImportDay } from '@/api/generated/import-batches/clients/review-import-day'
import { reviewImportEntry } from '@/api/generated/import-batches/clients/review-import-entry'
import type { ImportBatchDto } from '@/api/generated/models/types/import-batch-dto'
import type { ImportDayReviewDto } from '@/api/generated/models/types/import-day-review-dto'
import type { ImportEntryPageDto } from '@/api/generated/models/types/import-entry-page-dto'
import type { ImportEntryReviewDto } from '@/api/generated/models/types/import-entry-review-dto'
import { Notification } from '@/components/notification'
import { SessionKeys } from '@/domain/sessions/apis/session.keys'
import { isProblemDetailsError, retryUnlessRefused } from '@/lib/problem-details'

import { ImportKeys } from './import.keys'

export function useImportBatches() {
  return useQuery({
    queryKey: ImportKeys.lists(),
    queryFn: ({ signal }) => getImportBatches({ signal, options: { suppressErrorToast: true } }),
    retry: retryUnlessRefused,
  })
}

export function useImportDays(id: string) {
  return useQuery({
    queryKey: ImportKeys.days(id),
    queryFn: ({ signal }) => getImportDays({ path: { id }, signal, options: { suppressErrorToast: true } }),
    retry: retryUnlessRefused,
  })
}

export function useImportEntries(id: string, from: string, to: string) {
  return useQuery({
    queryKey: ImportKeys.entries(id, from, to),
    queryFn: ({ signal }) => getImportEntries({ path: { id }, query: { from, to }, signal, options: { suppressErrorToast: true } }),
    retry: retryUnlessRefused,
  })
}

export const isOutdated = (error: Error) => isProblemDetailsError(error) && (error.status === 404 || error.status === 409)

function readAgainAfterARefusal(queryClient: QueryClient, id: string, error: Error) {
  if (isOutdated(error)) {
    void queryClient.invalidateQueries({ queryKey: ImportKeys.lists() })
    void queryClient.invalidateQueries({ queryKey: ImportKeys.batch(id) })
  }
}

export function useDiscardImportBatch() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (id: string) => discardImportBatch({ path: { id } }),
    onSettled: () => queryClient.invalidateQueries({ queryKey: ImportKeys.lists() }),
  })
}

export function useReviewImportEntry(id: string, { inForm = false } = {}) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationKey: ImportKeys.reviews(id),
    mutationFn: (review: ImportEntryReviewDto) =>
      reviewImportEntry({ path: { id }, body: review, options: { suppressErrorToast: inForm } }),
    onSuccess: ({ entry }) => {
      queryClient.setQueriesData<ImportEntryPageDto>({ queryKey: ImportKeys.entriesOf(id) }, (page) =>
        page && {
          ...page,
          entries: page.entries.map((e) => (e.clientSessionId === entry.clientSessionId ? entry : e)),
        })
      return queryClient.invalidateQueries({ queryKey: ImportKeys.batch(id) })
    },
    onError: (error) => {
      if (inForm && isOutdated(error)) Notification.error(error.message)
      readAgainAfterARefusal(queryClient, id, error)
    },
  })
}

export function useReviewImportDay(id: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationKey: ImportKeys.reviews(id),
    mutationFn: (review: ImportDayReviewDto) => reviewImportDay({ path: { id }, body: review }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ImportKeys.batch(id) }),
    onError: (error) => readAgainAfterARefusal(queryClient, id, error),
  })
}

/** True while a review of an entry or a day of the batch, with the read of the days after it, is not done. */
export const useIsReviewing = (id: string) => useIsMutating({ mutationKey: ImportKeys.reviews(id) }) > 0

export function useAcceptImportBatch(id: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (version: number) => acceptImportBatch({ path: { id }, body: { version }, options: { suppressErrorToast: true } }),
    onSuccess: (batch) => {
      queryClient.setQueryData<ImportBatchDto[]>(ImportKeys.lists(), (batches) => batches?.map((b) => (b.id === batch.id ? batch : b)))
      void queryClient.invalidateQueries({ queryKey: SessionKeys.all })
      return queryClient.invalidateQueries({ queryKey: ImportKeys.lists() })
    },
    onError: (error) => readAgainAfterARefusal(queryClient, id, error),
  })
}
