import { afterEach, describe, expect, it, vi } from 'vitest'
import type { Filter } from '../types'
import { createGroup, newFilterId } from './filter-state'

const filter = (id: string): Filter => ({ id, propertyKey: 'name', propertyLabel: 'Name', controlType: 'text', operator: '@=', value: 'a' })

describe('filter IDs on a page that is not a secure context', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
  })

  const stubInsecureContext = () => {
    vi.stubGlobal('crypto', { getRandomValues: crypto.getRandomValues.bind(crypto) })
    expect('randomUUID' in crypto).toBe(false)
  }

  it('makes a new ID each time with no crypto.randomUUID', () => {
    stubInsecureContext()

    const ids = new Set(Array.from({ length: 100 }, () => newFilterId()))

    expect(ids.size).toBe(100)
    for (const id of ids) expect(id).toMatch(/^[0-9a-f]{32}$/)
  })

  it('groups filters with no crypto.randomUUID', () => {
    stubInsecureContext()
    const state = { filters: [filter('a'), filter('b')], rootLogicalOperator: 'AND' as const }

    const grouped = createGroup(state, ['a', 'b'], 'AND')

    expect(grouped.filters).toHaveLength(1)
    expect(grouped.filters[0].id).toMatch(/^[0-9a-f]{32}$/)
  })
})
