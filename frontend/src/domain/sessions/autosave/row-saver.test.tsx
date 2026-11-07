import { afterEach, beforeEach, expect, test, vi } from 'vitest'

import { ProblemDetailsError, STALE_VERSION_PROBLEM_TYPE } from '@/lib/problem-details'

import { planAfterStale, RowSaver, type RowSaverState } from './row-saver'

interface Fields {
  capture: boolean
  zone: string | null
  topics: string[]
}

interface Row extends Fields {
  version: number
}

const stale = () => new ProblemDetailsError({ type: STALE_VERSION_PROBLEM_TYPE, title: 'Changed', status: 409 })

const rules = {
  asksFirst: (field: keyof Fields, value: Fields[keyof Fields]) => field === 'capture' && value === true,
  neverAsks: (field: keyof Fields, value: Fields[keyof Fields]) => field === 'capture' && value === false,
}

const fieldsOf = ({ capture, zone, topics }: Row): Fields => ({ capture, zone, topics })

function server(start: Partial<Row> = {}) {
  const row: { current: Row } = { current: { capture: false, zone: 'UTC', topics: [], version: 1, ...start } }
  const puts: { values: Fields; version: number }[] = []
  let reads = 0
  let isDown = false
  return {
    row,
    puts,
    reads: () => reads,
    goesDown() {
      isDown = true
    },
    otherClientSets(patch: Partial<Row>) {
      row.current = { ...row.current, ...patch, version: row.current.version + 1 }
    },
    read: () => {
      reads += 1
      return Promise.resolve(row.current)
    },
    put: (values: Fields, version: number) => {
      puts.push({ values, version })
      if (isDown) return Promise.reject(new Error('Network Error'))
      if (version !== row.current.version) return Promise.reject(stale())
      row.current = { ...values, version: version + 1 }
      return Promise.resolve(row.current)
    },
  }
}

function saverOn(api: ReturnType<typeof server>, copy: { current: Row }, more: { onState?: (state: RowSaverState<Fields> | null) => void } = {}) {
  const told = vi.fn()
  const saver = new RowSaver<Fields, Row>({
    copyOf: () => copy.current,
    read: async () => {
      copy.current = await api.read()
      return copy.current
    },
    fieldsOf,
    versionOf: (row) => row.version,
    put: (values, version) => api.put(values, version),
    onReply: (row) => {
      copy.current = row
    },
    rules,
    onAppliedWithNoAsk: told,
    onStateChange: more.onState,
  })
  return { saver, told }
}

beforeEach(() => {
  vi.useFakeTimers()
})

afterEach(() => {
  vi.useRealTimers()
})

test('a body comes from the copy and the changed field, with the version of the copy', async () => {
  const api = server({ zone: 'Asia/Kolkata', version: 4 })
  const { saver } = saverOn(api, { current: api.row.current })

  saver.edit({ topics: ['work'] })
  await vi.advanceTimersByTimeAsync(0)

  expect(api.puts).toEqual([{ values: { capture: false, zone: 'Asia/Kolkata', topics: ['work'] }, version: 4 }])
  saver.dispose()
})

test('a stale 409 reads the row, builds the body from the new row, and the other client keeps its field', async () => {
  const api = server()
  const copy = { current: api.row.current }
  const { saver } = saverOn(api, copy)
  api.otherClientSets({ zone: 'Africa/Cairo' })

  saver.edit({ topics: ['work'] })
  await vi.advanceTimersByTimeAsync(0)

  expect(api.puts).toHaveLength(2)
  expect(api.row.current).toEqual({ capture: false, zone: 'Africa/Cairo', topics: ['work'], version: 3 })
  expect(saver.getStatus().kind).toBe('saved')
  expect(saver.asks()).toEqual([])
  saver.dispose()
})

test('a stale save makes no more requests after it ends', async () => {
  const api = server()
  const { saver } = saverOn(api, { current: api.row.current })
  api.otherClientSets({ zone: 'Africa/Cairo' })

  saver.edit({ topics: ['work'] })
  await vi.advanceTimersByTimeAsync(120_000)

  expect(api.puts).toHaveLength(2)
  expect(api.reads()).toBe(1)
  saver.dispose()
})

test('the same field that changed on the server waits for the person, and nothing goes until the answer', async () => {
  const api = server({ topics: ['a'] })
  const { saver } = saverOn(api, { current: api.row.current })
  api.otherClientSets({ topics: ['a', 'b'] })

  saver.edit({ topics: ['a', 'mine'] })
  await vi.advanceTimersByTimeAsync(0)

  expect(api.puts).toHaveLength(1)
  expect(api.row.current.topics).toEqual(['a', 'b'])
  expect(saver.asks()).toEqual([{ field: 'topics', yours: ['a', 'mine'], now: ['a', 'b'] }])

  saver.answerAsk(saver.asks()[0], true)
  await vi.advanceTimersByTimeAsync(0)

  expect(api.row.current.topics).toEqual(['a', 'mine'])
  expect(saver.asks()).toEqual([])
  saver.dispose()
})

test('keep what is there sends nothing', async () => {
  const api = server({ topics: ['a'] })
  const { saver } = saverOn(api, { current: api.row.current })
  api.otherClientSets({ topics: ['a', 'b'] })
  saver.edit({ topics: ['mine'] })
  await vi.advanceTimersByTimeAsync(0)

  saver.answerAsk(saver.asks()[0], false)
  await vi.advanceTimersByTimeAsync(60_000)

  expect(api.puts).toHaveLength(1)
  expect(api.row.current.topics).toEqual(['a', 'b'])
  saver.dispose()
})

test('a change that turns capture on asks after a 409, also when capture did not change on the server', async () => {
  const api = server()
  const { saver } = saverOn(api, { current: api.row.current })
  api.otherClientSets({ zone: 'Africa/Cairo' })

  saver.edit({ capture: true })
  await vi.advanceTimersByTimeAsync(0)

  expect(api.row.current.capture).toBe(false)
  expect(saver.asks()).toEqual([{ field: 'capture', yours: true, now: false }])
  saver.dispose()
})

test('an edit that comes during a save that meets a stale row is the value that the ask offers', async () => {
  const api = server()
  const { saver } = saverOn(api, { current: api.row.current })
  api.otherClientSets({ topics: ['theirs'] })
  const { put } = api
  let isFirst = true
  api.put = (values, version) => {
    if (isFirst) saver.edit({ topics: ['a', 'b'] })
    isFirst = false
    return put(values, version)
  }

  saver.edit({ topics: ['a'] })
  await vi.advanceTimersByTimeAsync(0)

  expect(saver.asks()).toEqual([{ field: 'topics', yours: ['a', 'b'], now: ['theirs'] }])
  saver.answerAsk(saver.asks()[0]!, true)
  await vi.advanceTimersByTimeAsync(0)
  expect(api.row.current.topics).toEqual(['a', 'b'])
  saver.dispose()
})

test('Keep what is there holds when a retry of the save still has the field', async () => {
  const api = server()
  const { saver } = saverOn(api, { current: api.row.current })
  api.otherClientSets({ zone: 'Africa/Cairo' })
  const { put } = api
  let puts = 0
  api.put = (values, version) => {
    puts += 1
    return puts === 2 ? Promise.reject(new Error('Network Error')) : put(values, version)
  }

  saver.edit({ zone: 'Europe/Athens', topics: ['work'] })
  await vi.advanceTimersByTimeAsync(0)
  saver.answerAsk(saver.asks()[0]!, false)
  await vi.advanceTimersByTimeAsync(60_000)

  expect(api.row.current).toMatchObject({ zone: 'Africa/Cairo', topics: ['work'] })
  expect(saver.pendingFields()).toEqual({})
  saver.dispose()
})

test('a field that the person kept as it is goes out again after a new change of that field', async () => {
  const api = server()
  const { saver } = saverOn(api, { current: api.row.current })
  api.otherClientSets({ zone: 'Africa/Cairo' })
  saver.edit({ zone: 'Europe/Athens' })
  await vi.advanceTimersByTimeAsync(0)
  saver.answerAsk(saver.asks()[0]!, false)

  saver.edit({ zone: 'Asia/Kolkata' })
  await vi.advanceTimersByTimeAsync(0)

  expect(api.row.current.zone).toBe('Asia/Kolkata')
  saver.dispose()
})

test('the note of a change with no ask comes after the Server took the change', async () => {
  const api = server({ capture: true })
  const { saver, told } = saverOn(api, { current: api.row.current })
  api.otherClientSets({ zone: 'Africa/Cairo' })
  const { put } = api
  let puts = 0
  let answer = () => undefined as void
  api.put = (values, version) => {
    puts += 1
    if (puts === 1) return put(values, version)
    return new Promise((resolve) => {
      answer = () => resolve(put(values, version))
    })
  }

  saver.edit({ capture: false })
  await vi.advanceTimersByTimeAsync(0)
  expect(puts).toBe(2)
  expect(told).not.toHaveBeenCalled()

  answer()
  await vi.advanceTimersByTimeAsync(0)
  expect(told).toHaveBeenCalledWith(['capture'])
  saver.dispose()
})

test('the note of a change with no ask also comes after a retry', async () => {
  const api = server({ capture: true })
  const { saver, told } = saverOn(api, { current: api.row.current })
  api.otherClientSets({ zone: 'Africa/Cairo' })
  const { put } = api
  let puts = 0
  api.put = (values, version) => {
    puts += 1
    return puts === 2 ? Promise.reject(new Error('Network Error')) : put(values, version)
  }

  saver.edit({ capture: false })
  await vi.advanceTimersByTimeAsync(60_000)

  expect(api.row.current.capture).toBe(false)
  expect(told).toHaveBeenCalledWith(['capture'])
  saver.dispose()
})

test('the note of a change with no ask comes once when the reply was lost, and never with a later change', async () => {
  const api = server({ capture: true })
  const { saver, told } = saverOn(api, { current: api.row.current })
  api.otherClientSets({ zone: 'Africa/Cairo' })
  const { put } = api
  let puts = 0
  api.put = async (values, version) => {
    puts += 1
    const reply = await put(values, version)
    if (puts === 2) throw new Error('Network Error')
    return reply
  }

  saver.edit({ capture: false })
  await vi.advanceTimersByTimeAsync(60_000)
  expect(told).toHaveBeenCalledTimes(1)

  saver.edit({ capture: true })
  await vi.advanceTimersByTimeAsync(60_000)
  expect(api.row.current.capture).toBe(true)
  expect(told).toHaveBeenCalledTimes(1)
  saver.dispose()
})

test('a person who takes the change back during the read of a stale round gets no note about it', async () => {
  const api = server({ capture: true })
  const { saver, told } = saverOn(api, { current: api.row.current })
  api.otherClientSets({ zone: 'Africa/Cairo' })
  const { read } = api
  let answerRead = () => undefined as void
  api.read = () =>
    new Promise((resolve) => {
      answerRead = () => resolve(read())
    })

  saver.edit({ capture: false })
  await vi.advanceTimersByTimeAsync(0)
  saver.edit({ capture: true })
  answerRead()
  await vi.advanceTimersByTimeAsync(60_000)

  expect(api.row.current.capture).toBe(true)
  expect(told).not.toHaveBeenCalled()
  saver.dispose()
})

test('an answer that can change keeps the note of a change with no ask', async () => {
  const api = server({ capture: true })
  const { saver, told } = saverOn(api, { current: api.row.current })
  api.otherClientSets({ zone: 'Africa/Cairo' })
  const { put } = api
  let puts = 0
  api.put = (values, version) => {
    puts += 1
    return puts === 2 ? Promise.reject(new ProblemDetailsError({ type: 'about:blank', status: 429, title: 'Too many requests' })) : put(values, version)
  }

  saver.edit({ capture: false })
  await vi.advanceTimersByTimeAsync(60_000)

  expect(api.row.current.capture).toBe(false)
  expect(told).toHaveBeenCalledWith(['capture'])
  saver.dispose()
})

test('a refused read ends the note of a change with no ask', async () => {
  const api = server({ capture: true })
  const { saver, told } = saverOn(api, { current: api.row.current })
  api.otherClientSets({ zone: 'Africa/Cairo' })
  const { put, read } = api
  let puts = 0
  let reads = 0
  api.put = (values, version) => {
    puts += 1
    if (puts === 2) {
      api.otherClientSets({ zone: 'Asia/Tokyo' })
      return Promise.reject(stale())
    }
    return put(values, version)
  }
  api.read = () => {
    reads += 1
    return reads === 2 ? Promise.reject(new ProblemDetailsError({ type: 'about:blank', status: 403, title: 'Refused' })) : read()
  }

  saver.edit({ capture: false })
  await vi.advanceTimersByTimeAsync(0)
  saver.edit({ topics: ['mine'] })
  await vi.advanceTimersByTimeAsync(60_000)

  expect(api.row.current.topics).toEqual(['mine'])
  expect(api.row.current.capture).toBe(true)
  expect(told).not.toHaveBeenCalled()
  saver.dispose()
})

test('a person who takes the change back during a retry gets no note about it', async () => {
  const api = server({ capture: true })
  const { saver, told } = saverOn(api, { current: api.row.current })
  api.otherClientSets({ zone: 'Africa/Cairo' })
  const { put } = api
  let puts = 0
  api.put = (values, version) => {
    puts += 1
    return puts === 2 ? Promise.reject(new Error('Network Error')) : put(values, version)
  }

  saver.edit({ capture: false })
  await vi.advanceTimersByTimeAsync(0)
  expect(puts).toBe(2)
  saver.edit({ capture: true })
  await vi.advanceTimersByTimeAsync(60_000)

  expect(api.row.current.capture).toBe(true)
  expect(told).not.toHaveBeenCalled()
  saver.dispose()
})

test('a refused change gets no note, also when a later change of another field works', async () => {
  const api = server({ capture: true })
  const { saver, told } = saverOn(api, { current: api.row.current })
  api.otherClientSets({ zone: 'Africa/Cairo' })
  const { put } = api
  let puts = 0
  api.put = (values, version) => {
    puts += 1
    return puts === 2 ? Promise.reject(new ProblemDetailsError({ type: 'about:blank', title: 'Refused', status: 422 })) : put(values, version)
  }

  saver.edit({ capture: false })
  await vi.advanceTimersByTimeAsync(0)
  expect(puts).toBe(2)
  saver.edit({ zone: 'Europe/Athens' })
  await vi.advanceTimersByTimeAsync(60_000)

  expect(api.row.current.zone).toBe('Europe/Athens')
  expect(told).not.toHaveBeenCalled()
  saver.dispose()
})

test('an unsaved change counts as unsaved, and a stopped saver does not', () => {
  const api = server()
  const { saver } = saverOn(api, { current: api.row.current })
  api.put = () => new Promise(() => undefined)

  expect(saver.hasUnsaved()).toBe(false)
  saver.edit({ zone: 'Europe/Athens' })
  expect(saver.hasUnsaved()).toBe(true)

  saver.dispose()
  expect(saver.hasUnsaved()).toBe(false)
})

test('a value that the app chose yields to the row, with no question', () => {
  const plan = planAfterStale<Fields>({
    changed: { zone: 'Europe/Athens', capture: true },
    before: { zone: '', capture: false, topics: [] },
    fresh: { zone: 'Africa/Cairo', capture: false, topics: [] },
    hadNoReply: false,
    rules: { yieldsToTheRow: (field, before) => field === 'zone' && before === '' },
  })

  expect(plan).toEqual({ apply: { capture: true }, asks: [], appliedWithNoAsk: [] })
})

test('a change that turns capture on applies with no prompt when the row did not change', async () => {
  const api = server()
  const { saver } = saverOn(api, { current: api.row.current })

  saver.edit({ capture: true })
  await vi.advanceTimersByTimeAsync(0)

  expect(api.row.current.capture).toBe(true)
  expect(saver.asks()).toEqual([])
  saver.dispose()
})

test('a change that turns capture off applies with no prompt over a newer capture on, and tells the person', async () => {
  const api = server({ capture: true })
  const { saver, told } = saverOn(api, { current: api.row.current })
  api.otherClientSets({ capture: false })
  api.otherClientSets({ capture: true })
  api.otherClientSets({ capture: true, zone: 'Africa/Cairo' })

  saver.edit({ capture: false })
  await vi.advanceTimersByTimeAsync(0)

  expect(api.row.current.capture).toBe(false)
  expect(api.row.current.zone).toBe('Africa/Cairo')
  expect(told).toHaveBeenCalledWith(['capture'])
  saver.dispose()
})

test('a field that the new row already holds sends nothing', async () => {
  const api = server()
  const { saver } = saverOn(api, { current: api.row.current })
  api.otherClientSets({ zone: 'Africa/Cairo' })

  saver.edit({ zone: 'Africa/Cairo' })
  await vi.advanceTimersByTimeAsync(0)

  expect(api.puts).toHaveLength(1)
  expect(saver.getStatus().kind).toBe('saved')
  saver.dispose()
})

test('a failure with no reply goes again with the same body and base, also after the copy moved', async () => {
  const api = server()
  const copy = { current: api.row.current }
  const { saver } = saverOn(api, copy)
  const put = vi.spyOn(api, 'put').mockRejectedValueOnce(new Error('Network Error'))

  saver.edit({ topics: ['work'] })
  await vi.advanceTimersByTimeAsync(0)
  copy.current = { ...copy.current, zone: 'Africa/Cairo', version: 9 }
  put.mockImplementation((values, version) => {
    api.puts.push({ values, version })
    return Promise.resolve({ ...values, version: version + 1 })
  })
  await vi.advanceTimersByTimeAsync(2_000)

  expect(api.puts).toEqual([{ values: { capture: false, zone: 'UTC', topics: ['work'] }, version: 1 }])
  saver.dispose()
})

test('a 409 after a save with no reply asks about a field that differs from the new row', async () => {
  const api = server()
  const { saver } = saverOn(api, { current: api.row.current })
  const put = vi.spyOn(api, 'put').mockRejectedValueOnce(new Error('Network Error'))

  saver.edit({ topics: ['work'] })
  await vi.advanceTimersByTimeAsync(0)
  api.otherClientSets({ zone: 'Africa/Cairo' })
  put.mockRestore()
  await vi.advanceTimersByTimeAsync(2_000)

  expect(saver.asks()).toEqual([{ field: 'topics', yours: ['work'], now: [] }])
  saver.dispose()
})

test('two changes of a field after no reply keep only the last, so capture on does not apply for a time', async () => {
  const api = server({ capture: false })
  const { saver } = saverOn(api, { current: api.row.current })
  const put = vi.spyOn(api, 'put').mockRejectedValueOnce(new Error('Network Error'))

  saver.edit({ capture: true })
  await vi.advanceTimersByTimeAsync(0)
  put.mockClear()
  saver.edit({ capture: false })
  saver.edit({ zone: 'Africa/Cairo' })
  await vi.advanceTimersByTimeAsync(2_000)

  expect(put.mock.calls.map(([values]) => values.capture)).not.toContain(true)
  expect(api.row.current.zone).toBe('Africa/Cairo')
  saver.dispose()
})

test('a change while a save is on the wire waits for its reply, and its body comes from that reply', async () => {
  const api = server()
  const { saver } = saverOn(api, { current: api.row.current })

  saver.edit({ zone: 'Africa/Cairo' })
  saver.edit({ topics: ['work'] })
  await vi.advanceTimersByTimeAsync(0)

  expect(api.puts.map(({ version }) => version)).toEqual([1, 2])
  expect(api.row.current).toMatchObject({ zone: 'Africa/Cairo', topics: ['work'], version: 3 })
  saver.dispose()
})

test('a row that changes on each try goes to the retry with a growing wait, and a new read each time', async () => {
  const api = server()
  const { saver } = saverOn(api, { current: api.row.current })
  const realPut = api.put
  vi.spyOn(api, 'put').mockImplementation((values, version) => {
    api.otherClientSets({ zone: `Zone/${api.row.current.version}` })
    return realPut(values, version)
  })

  saver.edit({ topics: ['work'] })
  await vi.advanceTimersByTimeAsync(30_000)

  expect(saver.getStatus().kind).toBe('waiting')
  expect(api.puts.length).toBeLessThanOrEqual(15)
  saver.dispose()
})

test('a change that resumes after a reload keeps its base, so a changed row gets a 409 and the prompt', async () => {
  const api = server({ capture: false })
  const base = { values: fieldsOf(api.row.current), version: api.row.current.version }
  api.otherClientSets({ zone: 'Africa/Cairo' })
  const { saver } = saverOn(api, { current: api.row.current })

  saver.adopt({ change: { value: { capture: true }, savedAt: '2026-11-06T00:00:00.000Z' }, attempt: { base, hadNoReply: false }, asks: [] })
  await vi.advanceTimersByTimeAsync(0)

  expect(api.row.current.capture).toBe(false)
  expect(saver.asks()).toEqual([{ field: 'capture', yours: true, now: false }])
  saver.dispose()
})

test('the base of a change is the row at the change, so a stale tab that sends later gets a 409', async () => {
  const api = server({ capture: false })
  const copy = { current: api.row.current }
  const states: (RowSaverState<Fields> | null)[] = []
  const { saver } = saverOn(api, copy, { onState: (state) => states.push(state) })
  api.goesDown()

  saver.edit({ capture: true })
  await vi.advanceTimersByTimeAsync(0)
  const kept = states.find((state) => state?.change)

  expect(kept?.attempt?.base.version).toBe(1)
  saver.dispose()
})

test('a stale key with a change of a field does not replace a later choice of the person in another tab', async () => {
  const api = server({ capture: true })
  const base = { values: fieldsOf(api.row.current), version: api.row.current.version }
  api.otherClientSets({ capture: false })
  const { saver } = saverOn(api, { current: api.row.current })

  saver.adopt({ change: { value: { capture: true }, savedAt: '2026-11-06T00:00:00.000Z' }, attempt: { base, hadNoReply: false }, asks: [] })
  await vi.advanceTimersByTimeAsync(0)

  expect(api.row.current.capture).toBe(false)
  expect(saver.asks()).toEqual([{ field: 'capture', yours: true, now: false }])
  saver.dispose()
})

test('a field that this tab changed after the stale key stays', async () => {
  const api = server({ zone: 'Africa/Cairo' })
  const base = { values: fieldsOf(api.row.current), version: api.row.current.version }
  api.goesDown()
  const { saver } = saverOn(api, { current: api.row.current })
  saver.edit({ zone: 'Asia/Tokyo' })
  await vi.advanceTimersByTimeAsync(0)

  saver.adopt({ change: { value: { zone: 'Europe/Paris', capture: true }, savedAt: '2000-01-01T00:00:00.000Z' }, attempt: { base, hadNoReply: false }, asks: [] })

  expect(saver.pendingFields()).toEqual({ zone: 'Asia/Tokyo', capture: true })
  saver.dispose()
})

test('an ask of a stale key joins the asks of this saver', () => {
  const api = server({ capture: false })
  const { saver } = saverOn(api, { current: api.row.current })

  saver.adopt({ change: null, attempt: null, asks: [{ field: 'capture', yours: true, now: false }] })

  expect(saver.asks()).toEqual([{ field: 'capture', yours: true, now: false }])
  saver.dispose()
})

test('the state to keep holds the base of a save and the asks, and is empty when all is settled', async () => {
  const api = server({ topics: ['a'] })
  const states: (RowSaverState<Fields> | null)[] = []
  const { saver } = saverOn(api, { current: api.row.current }, { onState: (state) => states.push(state) })

  saver.edit({ zone: 'Africa/Cairo' })
  await vi.advanceTimersByTimeAsync(0)

  expect(states[states.length - 1]).toBeNull()
  expect(states.some((state) => state?.attempt?.base.version === 1)).toBe(true)

  api.otherClientSets({ topics: ['a', 'b'] })
  saver.edit({ topics: ['mine'] })
  await vi.advanceTimersByTimeAsync(0)

  expect(states[states.length - 1]?.asks).toHaveLength(1)
  saver.dispose()
})

test('the plan drops a field that the row holds, in any order of the items of a list it does not reorder', () => {
  const plan = planAfterStale<Fields>({
    changed: { topics: ['a'], zone: 'B' },
    before: { capture: false, zone: 'A', topics: [] },
    fresh: { capture: false, zone: 'A', topics: ['a'] },
    hadNoReply: false,
  })

  expect(plan).toEqual({ apply: { zone: 'B' }, asks: [], appliedWithNoAsk: [] })
})
