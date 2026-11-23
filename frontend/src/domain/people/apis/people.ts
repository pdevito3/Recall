import { queryOptions, useMutation, useQueries, useQuery, useQueryClient } from '@tanstack/react-query'

import type { PersonDetailsDto } from '@/api/generated/models/types/person-details-dto'
import type { PersonDto } from '@/api/generated/models/types/person-dto'
import { addPerson } from '@/api/generated/people/clients/add-person'
import { deletePerson } from '@/api/generated/people/clients/delete-person'
import { describePerson } from '@/api/generated/people/clients/describe-person'
import { getPerson } from '@/api/generated/people/clients/get-person'
import { getPersonList } from '@/api/generated/people/clients/get-person-list'
import { renamePerson } from '@/api/generated/people/clients/rename-person'
import { parsePagination } from '@/lib/pagination'
import { isProblemDetailsError, retryUnlessRefused } from '@/lib/problem-details'

import { SessionKeys } from '@/domain/sessions/apis/session.keys'

import { PersonKeys } from './person.keys'

const directoryPageSize = 100

async function readDirectory(signal: AbortSignal): Promise<PersonDto[]> {
  const people: PersonDto[] = []
  for (let pageNumber = 1; ; pageNumber++) {
    const result = await getPersonList({
      query: { pageNumber, pageSize: directoryPageSize },
      signal,
      options: { suppressErrorToast: true },
    })
    people.push(...result.data)
    if (!parsePagination(result).hasNext) return people
  }
}

const peopleDirectoryOptions = queryOptions({
  queryKey: PersonKeys.directory(),
  queryFn: ({ signal }) => readDirectory(signal),
  staleTime: 60_000,
  retry: retryUnlessRefused,
})

export function usePeopleDirectory() {
  return useQuery(peopleDirectoryOptions)
}

export function personOptions(id: string) {
  return queryOptions({
    queryKey: PersonKeys.detail(id),
    queryFn: ({ signal }) => getPerson({ path: { id }, signal, options: { suppressErrorToast: true } }),
    staleTime: 60_000,
    retry: retryUnlessRefused,
  })
}

/**
 * The People of these IDs, in their order. The directory gives each one it holds. An ID that the cached directory lacks
 * is a Person added since the directory loaded, so it is read on its own. Only a 404 leaves it out. Any other failure
 * is the `error`, because the list is then shorter than the Server's and must not be offered as the whole choice.
 */
export function usePeopleOf(ids: readonly string[]) {
  const directory = usePeopleDirectory()
  const held = new Map(directory.data?.map((person) => [person.id, person]))
  const unheld = useQueries({
    queries: directory.data ? ids.filter((id) => !held.has(id)).map((id) => personOptions(id)) : [],
  })
  const read = new Map(unheld.flatMap((query) => (query.data ? [[query.data.id, query.data] as const] : [])))
  const failed = unheld.find((query) => !query.data && query.isError && !isNotFound(query.error))
  return {
    people: ids.flatMap((id) => held.get(id) ?? read.get(id) ?? []),
    isPending: directory.isPending || unheld.some((query) => query.isPending),
    error: directory.data ? (failed?.error ?? null) : directory.error,
    retry: () => (directory.data ? failed?.refetch() : directory.refetch()),
  }
}

const isNotFound = (error: unknown) => isProblemDetailsError(error) && error.status === 404

export function useAddPerson() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (label: string) => addPerson({ body: { label }, options: { suppressErrorToast: true } }),
    // A new Person can be a match of a People proposal, so an open entry reads its matches again.
    onSuccess: async () => {
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: PersonKeys.all }),
        queryClient.invalidateQueries({ queryKey: SessionKeys.all }),
      ])
    },
  })
}

export function useRenamePerson() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({ id, label }: { id: string; label: string }) =>
      renamePerson({ path: { id }, body: { label }, options: { suppressErrorToast: true } }),
    onSuccess: async () => {
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: PersonKeys.all }),
        queryClient.invalidateQueries({ queryKey: SessionKeys.all }),
      ])
    },
  })
}

export function useDescribePerson() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({ id, body }: { id: string; body: PersonDetailsDto }) =>
      describePerson({ path: { id }, body, options: { suppressErrorToast: true } }),
    onMutate: ({ id }) => queryClient.cancelQueries({ queryKey: PersonKeys.detail(id) }),
    onSuccess: (person) => {
      queryClient.setQueryData(PersonKeys.detail(person.id), person)
      return queryClient.invalidateQueries({ queryKey: PersonKeys.directory() })
    },
  })
}

/** A refused delete also refetches: after a 409 the list shows the name of the rename, and after a 404 it drops the Person. */
export function useDeletePerson() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: ({ id, version }: { id: string; version: number }) => deletePerson({ path: { id }, query: { ifVersion: version } }),
    onSettled: async () => {
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: PersonKeys.all }),
        queryClient.invalidateQueries({ queryKey: SessionKeys.all }),
      ])
    },
  })
}
