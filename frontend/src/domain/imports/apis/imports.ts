import { useMutation, useQuery, useQueryClient, type QueryClient } from '@tanstack/react-query'

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

/** A 404 of an entry that the agent removed, or a 409 of a batch that an accept closed elsewhere: the page reads the batch again. */
/** A 404 or a 409 says that the entry or the batch changed, so the page that shows it is old. */
export const isGone = (error: Error) => isProblemDetailsError(error) && (error.status === 404 || error.status === 409)

function readAgainAfterARefusal(queryClient: QueryClient, id: string, error: Error) {
  if (isGone(error)) {
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

/** In a form, the form shows a refusal itself, so the call shows no toast. */
export function useReviewImportEntry(id: string, { inForm = false } = {}) {
  const queryClient = useQueryClient()
  return useMutation({
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
      // The reread takes the form off the page with the entry, so the message goes in a toast.
      if (inForm && isGone(error)) Notification.error(error.message)
      readAgainAfterARefusal(queryClient, id, error)
    },
  })
}

export function useReviewImportDay(id: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (review: ImportDayReviewDto) => reviewImportDay({ path: { id }, body: review }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ImportKeys.batch(id) }),
    onError: (error) => readAgainAfterARefusal(queryClient, id, error),
  })
}

/** The dialog of the accept shows a refusal, so the call shows no toast. */
export function useAcceptImportBatch(id: string) {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (version: number) => acceptImportBatch({ path: { id }, body: { version }, options: { suppressErrorToast: true } }),
    onSuccess: (batch) => {
      queryClient.setQueryData<ImportBatchDto[]>(ImportKeys.lists(), (batches) => batches?.map((b) => (b.id === batch.id ? batch : b)))
      void queryClient.invalidateQueries({ queryKey: SessionKeys.all })
      return queryClient.invalidateQueries({ queryKey: ImportKeys.lists() })
    },
    onError: (error) => {
      if (isProblemDetailsError(error) && error.status === 409) void queryClient.invalidateQueries({ queryKey: ImportKeys.batch(id) })
    },
  })
}
