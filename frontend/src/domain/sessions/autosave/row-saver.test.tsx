import { afterEach, beforeEach, expect, test, vi } from 'vitest'

import { ProblemDetailsError, STALE_VERSION_PROBLEM_TYPE } from '@/lib/problem-details'

import { planAfterStale, RowSaver, type RowSaverState, type StaleRules } from './row-saver'

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

const rulesOfLocation: StaleRules<Fields> = { ...rules, onlyFromALiveClick: (field, value) => field === 'capture' && value === true }

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
    comesBack() {
      isDown = false
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

function saverOn(api: ReturnType<typeof server>, copy: { current: Row }, more: { onState?: (state: RowSaverState<Fields> | null) => void; rules?: StaleRules<Fields> } = {}) {
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
    rules: more.rules ?? rules,
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

test('a stale key that turns capture off asks, and does not go over a later choice of on', async () => {
  const api = server({ capture: true })
  const base = { values: fieldsOf(api.row.current), version: api.row.current.version }
  api.otherClientSets({ zone: 'Africa/Cairo' })
  api.otherClientSets({ capture: true })
  const { saver } = saverOn(api, { current: api.row.current })

  saver.adopt({ change: { value: { capture: false }, savedAt: '2026-11-06T00:00:00.000Z' }, attempt: { base, hadNoReply: true }, asks: [] })
  await vi.advanceTimersByTimeAsync(0)

  expect(api.row.current.capture).toBe(true)
  expect(saver.asks()).toEqual([{ field: 'capture', yours: false, now: true }])
  saver.dispose()
})

test.each([
  { when: 'after', savedAt: '2026-11-06T11:00:00.000Z', capture: false },
  { when: 'before', savedAt: '2026-11-06T09:00:00.000Z', capture: true },
])('a change of a tab that left, made $when the answer to keep the field', async ({ savedAt, capture }) => {
  vi.setSystemTime(new Date('2026-11-06T10:00:00.000Z'))
  const api = server({ capture: true })
  const copy = { current: api.row.current }
  const { saver } = saverOn(api, copy)
  saver.adopt({ change: null, attempt: null, asks: [{ field: 'capture', yours: false, now: true }] })
  await vi.advanceTimersByTimeAsync(0)
  saver.answerAsk(saver.asks()[0], false)

  saver.adopt({ change: { value: { capture: false }, savedAt }, attempt: null, asks: [] })
  await vi.advanceTimersByTimeAsync(0)

  expect(api.row.current.capture).toBe(capture)
  saver.dispose()
})

test('a change older than the answer to keep its field leaves nothing behind', async () => {
  vi.setSystemTime(new Date('2026-11-06T10:00:00.000Z'))
  const api = server({ capture: true })
  const base = { values: fieldsOf(api.row.current), version: api.row.current.version }
  api.otherClientSets({ zone: 'Africa/Cairo' })
  const { saver } = saverOn(api, { current: api.row.current })
  saver.adopt({ change: null, attempt: null, asks: [{ field: 'capture', yours: false, now: true }] })
  await vi.advanceTimersByTimeAsync(0)
  saver.answerAsk(saver.asks()[0], false)

  saver.adopt({ change: { value: { capture: false }, savedAt: '2026-11-06T09:00:00.000Z' }, attempt: { base, hadNoReply: true }, asks: [] })
  await vi.advanceTimersByTimeAsync(0)
  expect(saver.getStatus().kind).toBe('idle')

  vi.setSystemTime(new Date('2026-11-06T11:00:00.000Z'))
  saver.edit({ capture: false })
  await vi.advanceTimersByTimeAsync(0)

  expect(api.row.current.capture).toBe(false)
  expect(saver.asks()).toEqual([])
  saver.dispose()
})

test('an ask of a stale key shows the value that the row has now', async () => {
  const api = server({ zone: 'Africa/Cairo' })
  const copy = { current: api.row.current }
  const { saver } = saverOn(api, copy)
  api.otherClientSets({ zone: 'Asia/Tokyo' })

  saver.adopt({ change: null, attempt: null, asks: [{ field: 'zone', yours: 'Europe/Paris', now: 'Africa/Cairo' }] })
  await vi.advanceTimersByTimeAsync(0)

  expect(saver.asks()).toEqual([{ field: 'zone', yours: 'Europe/Paris', now: 'Asia/Tokyo' }])
  saver.dispose()
})

test('an ask of a stale key takes the value of the row from a reply, when the read for the ask failed', async () => {
  const api = server({ zone: 'Africa/Cairo' })
  api.otherClientSets({ zone: 'Asia/Tokyo' })
  const { saver } = saverOn(api, { current: api.row.current })
  const read = api.read
  api.read = () => Promise.reject(new Error('The read stopped.'))

  saver.adopt({ change: { value: { topics: ['work'] }, savedAt: '2026-11-06T00:00:00.000Z' }, attempt: null, asks: [{ field: 'zone', yours: 'Europe/Paris', now: 'Africa/Cairo' }] })
  await vi.advanceTimersByTimeAsync(0)
  api.read = read

  expect(saver.asks()).toEqual([{ field: 'zone', yours: 'Europe/Paris', now: 'Asia/Tokyo' }])
  saver.dispose()
})

test('an ask of a stale key goes when the row already has that choice', async () => {
  const api = server({ zone: 'Africa/Cairo' })
  const { saver } = saverOn(api, { current: api.row.current })
  api.otherClientSets({ zone: 'Europe/Paris' })

  saver.adopt({ change: null, attempt: null, asks: [{ field: 'zone', yours: 'Europe/Paris', now: 'Africa/Cairo' }] })
  await vi.advanceTimersByTimeAsync(0)

  expect(saver.asks()).toEqual([])
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

type Click = 'on' | 'off' | 'other'

const clicksOf: Record<Click, Partial<Fields>> = { on: { capture: true }, off: { capture: false }, other: { zone: 'Asia/Tokyo' } }

/** Each order of the clicks of two tabs, with the order inside each tab kept. */
function ordersOf(one: Click[], other: Click[]): [Click, 'A' | 'B'][][] {
  if (one.length === 0) return [other.map((click) => [click, 'B'])]
  if (other.length === 0) return [one.map((click) => [click, 'A'])]
  return [
    ...ordersOf(one.slice(1), other).map((rest): [Click, 'A' | 'B'][] => [[one[0] as Click, 'A'], ...rest]),
    ...ordersOf(one, other.slice(1)).map((rest): [Click, 'A' | 'B'][] => [[other[0] as Click, 'B'], ...rest]),
  ]
}

async function twoTabsOffline(start: Partial<Row>, order: [Click, 'A' | 'B'][]) {
  const api = server(start)
  api.goesDown()
  const copyA = { current: api.row.current }
  const copyB = { current: api.row.current }
  let keyOfA: RowSaverState<Fields> | null = null
  const a = saverOn(api, copyA, { onState: (state) => { keyOfA = state }, rules: rulesOfLocation }).saver
  const b = saverOn(api, copyB, { rules: rulesOfLocation }).saver
  for (const [click, tab] of order) {
    ;(tab === 'A' ? a : b).edit(clicksOf[click])
    await vi.advanceTimersByTimeAsync(10)
  }
  a.dispose()
  const taken = keyOfA as RowSaverState<Fields> | null
  if (taken) b.adopt(taken)
  api.comesBack()
  const putsBeforeTheReturn = api.puts.length
  await vi.advanceTimersByTimeAsync(40_000)
  return { api, saver: b, putsAfterTheReturn: api.puts.slice(putsBeforeTheReturn) }
}

const firstOrders = ordersOf(['on', 'other'], ['on', 'off'])

test.each(firstOrders.map((order) => [order.map(([click, tab]) => `${tab} ${click}`).join(', '), order] as const))('row off, offline: %s. No turn on goes over the wire', async (_, order) => {
  const { api, saver, putsAfterTheReturn } = await twoTabsOffline({ capture: false }, order)

  expect(api.row.current.capture).toBe(false)
  expect(putsAfterTheReturn.filter(({ values }) => values.capture)).toEqual([])
  saver.dispose()
})

const secondOrders = ordersOf(['off', 'on', 'other'], ['off'])

test.each(secondOrders.map((order) => [order.map(([click, tab]) => `${tab} ${click}`).join(', '), order] as const))('row on, offline: %s. The later off holds', async (_, order) => {
  const { api, saver } = await twoTabsOffline({ capture: true }, order)
  expect(api.row.current.capture).toBe(false)
  saver.dispose()
})

test('a click on with the Server up goes at once', async () => {
  const api = server({ capture: false })
  const { saver } = saverOn(api, { current: api.row.current }, { rules: rulesOfLocation })

  saver.edit({ capture: true })
  await vi.advanceTimersByTimeAsync(10)

  expect(api.row.current.capture).toBe(true)
  expect(saver.asks()).toEqual([])
  saver.dispose()
})

test('a click on that gets no reply does not go again, and the page asks with the row in the question', async () => {
  const api = server({ capture: false })
  api.goesDown()
  const { saver } = saverOn(api, { current: api.row.current }, { rules: rulesOfLocation })

  saver.edit({ capture: true })
  await vi.advanceTimersByTimeAsync(10)
  expect(saver.asks()).toEqual([expect.objectContaining({ field: 'capture', yours: true, now: false, didNotReachTheServer: true })])
  expect(saver.pendingFields()).toEqual({})

  api.comesBack()
  await vi.advanceTimersByTimeAsync(40_000)
  expect(api.puts).toHaveLength(1)
  expect(api.row.current.capture).toBe(false)
  expect(saver.asks()).toHaveLength(1)

  const [ask] = saver.asks()
  if (ask) saver.answerAsk(ask, true)
  await vi.advanceTimersByTimeAsync(10)
  expect(api.row.current.capture).toBe(true)
  expect(saver.asks()).toEqual([])
  saver.dispose()
})

test('a click on whose reply was lost ends the ask, because the row holds the choice', async () => {
  const api = server({ capture: false })
  const copy = { current: api.row.current }
  let isReplyLost = true
  const { saver } = saverOn({ ...api, put: (values, version) => {
    const answer = api.put(values, version)
    if (!isReplyLost) return answer
    isReplyLost = false
    return answer.then(() => Promise.reject(new Error('Network Error')))
  } }, copy, { rules: rulesOfLocation })

  saver.edit({ capture: true })
  await vi.advanceTimersByTimeAsync(40_000)

  expect(api.row.current.capture).toBe(true)
  expect(saver.asks()).toEqual([])
  saver.dispose()
})

test('a change of another setting after a failed turn on goes without the turn on', async () => {
  const api = server({ capture: false })
  api.goesDown()
  const { saver } = saverOn(api, { current: api.row.current }, { rules: rulesOfLocation })
  saver.edit({ capture: true })
  await vi.advanceTimersByTimeAsync(10)
  saver.edit({ zone: 'Asia/Tokyo' })
  await vi.advanceTimersByTimeAsync(10)

  api.comesBack()
  await vi.advanceTimersByTimeAsync(40_000)

  expect(api.row.current).toMatchObject({ capture: false, zone: 'Asia/Tokyo' })
  expect(saver.asks()).toHaveLength(1)
  saver.dispose()
})

test('no key holds a turn on, and a key from an older version with a turn on does not send it', async () => {
  const states: (RowSaverState<Fields> | null)[] = []
  const api = server({ capture: false })
  api.goesDown()
  const { saver } = saverOn(api, { current: api.row.current }, { onState: (state) => states.push(state), rules: rulesOfLocation })
  saver.edit({ capture: true })
  await vi.advanceTimersByTimeAsync(10)
  saver.edit({ zone: 'Asia/Tokyo' })
  await vi.advanceTimersByTimeAsync(10)
  expect(JSON.stringify(states)).not.toContain('"capture":true')
  saver.dispose()

  const next = server({ capture: false })
  const { saver: taker } = saverOn(next, { current: next.row.current }, { rules: rulesOfLocation })
  taker.adopt({ change: { value: { capture: true, zone: 'Asia/Tokyo' }, savedAt: '2026-11-06T00:00:00.000Z' }, attempt: { base: { values: fieldsOf(next.row.current), version: 1 }, hadNoReply: true }, asks: [{ field: 'capture', yours: true, now: false }] })
  await vi.advanceTimersByTimeAsync(40_000)

  expect(next.row.current).toMatchObject({ capture: false, zone: 'Asia/Tokyo' })
  expect(taker.asks()).toEqual([])
  taker.dispose()
})

test('a click on after the row moved in a way that the page did not show asks and sends nothing', async () => {
  const api = server({ capture: false, version: 5 })
  const copy = { current: api.row.current }
  const { saver } = saverOn(api, copy, { rules: rulesOfLocation })
  let release: () => void = () => undefined
  const held = new Promise<void>((resolve) => { release = resolve })
  const realPut = api.put
  const { saver: slow } = saverOn({ ...api, put: async (values, version) => { await held; return realPut(values, version) } }, copy, { rules: rulesOfLocation })
  slow.edit({ zone: 'Asia/Tokyo' })
  await vi.advanceTimersByTimeAsync(10)
  slow.edit({ capture: true })
  await vi.advanceTimersByTimeAsync(10)

  api.otherClientSets({ capture: true })
  api.otherClientSets({ capture: false })
  release()
  await vi.advanceTimersByTimeAsync(40_000)

  expect(api.row.current.capture).toBe(false)
  expect(slow.asks()).toEqual([expect.objectContaining({ field: 'capture', yours: true, now: false })])
  saver.dispose()
  slow.dispose()
})

test('your own save does not make your next click on ask', async () => {
  const api = server({ capture: false })
  const copy = { current: api.row.current }
  const { saver } = saverOn(api, copy, { rules: rulesOfLocation })
  saver.edit({ zone: 'Asia/Tokyo' })
  saver.edit({ capture: true })
  await vi.advanceTimersByTimeAsync(10)

  expect(api.row.current).toMatchObject({ capture: true, zone: 'Asia/Tokyo' })
  expect(saver.asks()).toEqual([])
  saver.dispose()
})

test('an answer to a failed turn on leaves no old base, so the next change of another setting raises no false question', async () => {
  const api = server({ capture: false })
  api.goesDown()
  const { saver } = saverOn(api, { current: api.row.current }, { rules: rulesOfLocation })
  saver.edit({ capture: true })
  await vi.advanceTimersByTimeAsync(10)
  api.comesBack()
  await vi.advanceTimersByTimeAsync(40_000)
  const [ask] = saver.asks()
  if (ask) saver.answerAsk(ask, false)
  await vi.advanceTimersByTimeAsync(10)
  api.otherClientSets({ topics: ['a'] })

  saver.edit({ zone: 'Asia/Tokyo' })
  await vi.advanceTimersByTimeAsync(40_000)

  expect(saver.asks()).toEqual([])
  expect(api.row.current).toMatchObject({ capture: false, zone: 'Asia/Tokyo', topics: ['a'] })
  saver.dispose()
})
