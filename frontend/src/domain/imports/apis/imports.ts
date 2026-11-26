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

const isOutdated = (error: Error) => isProblemDetailsError(error) && (error.status === 404 || error.status === 409)

/**
 * Reads the batches and the batch again after a refusal that says the page is old. The mutation returns this from its
 * `onError`, so it stays pending until the new counts and version are in: Accept stays off, and the dialog of a stale
 * accept shows the new counts with its message.
 */
async function readAgainAfterARefusal(queryClient: QueryClient, id: string, error: Error): Promise<void> {
  if (!isOutdated(error)) return
  await Promise.all([
    queryClient.invalidateQueries({ queryKey: ImportKeys.lists() }),
    queryClient.invalidateQueries({ queryKey: ImportKeys.batch(id) }),
  ])
}

export function useDiscardImportBatch() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (id: string) => discardImportBatch({ path: { id } }),
    onSettled: () => queryClient.invalidateQueries({ queryKey: ImportKeys.lists() }),
  })
}

interface ReviewImportEntryOptions {
  /** True for a review from the form of an edit, which shows each other refusal itself. */
  inForm?: boolean
  /** Runs when the refusal says that the page is old, before the read again can take the entry off the page. */
  onOutdated?: () => void
}

export function useReviewImportEntry(id: string, { inForm = false, onOutdated }: ReviewImportEntryOptions = {}) {
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
      if (isOutdated(error)) {
        if (inForm) Notification.error(error.message)
        onOutdated?.()
      }
      return readAgainAfterARefusal(queryClient, id, error)
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
