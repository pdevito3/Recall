import type { PersonDto } from '@/api/generated/models/types/person-dto'
import type { MockRoute } from './mock-route'
import { paginationHeaders } from './pagination'

export const peoplePaths = {
  list: '/api/v1/people',
  one: (id: string) => `/api/v1/people/${id}`,
}

export const aPerson = (id: string, label: string): PersonDto => ({ id, label, version: 1 })

export const peopleApi = {
  listIs(people: PersonDto[]): MockRoute {
    return { method: 'get', path: peoplePaths.list, status: 200, headers: paginationHeaders(people.length, 1), body: people }
  },

  adds(person: PersonDto): MockRoute {
    return { method: 'post', path: peoplePaths.list, status: 201, body: person }
  },

  renames(person: PersonDto): MockRoute {
    return { method: 'put', path: peoplePaths.one(person.id), status: 200, body: person }
  },
}
