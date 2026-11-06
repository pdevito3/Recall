import { afterEach, beforeEach, expect, test, vi } from 'vitest'

import { CONCURRENT_CHANGE_PROBLEM_TYPE, ProblemDetailsError, STALE_VERSION_PROBLEM_TYPE } from '@/lib/problem-details'

import { SerialSaver } from './serial-saver'

const refused = (status: number, detail: string) => new ProblemDetailsError({ type: 'about:blank', title: 'Refused', status, detail })

type Send = (change: { value: string; savedAt: string }) => Promise<string>

function saverWith(send: Send, debounceMs = 0) {
  const onReply = vi.fn()
  return { saver: new SerialSaver<string, string>({ debounceMs, send, onReply }), onReply }
}

beforeEach(() => {
  vi.useFakeTimers()
})

afterEach(() => {
  vi.useRealTimers()
})

test('edits while a save is on the wire wait, then the newest goes alone', async () => {
  const replies: ((reply: string) => void)[] = []
  const send = vi.fn<Send>(() => new Promise<string>((resolve) => replies.push(resolve)))
  const { saver } = saverWith(send)

  saver.edit('a')
  saver.edit('ab')
  saver.edit('abc')
  expect(send).toHaveBeenCalledTimes(1)

  replies[0]('ok')
  await vi.advanceTimersByTimeAsync(0)

  expect(send).toHaveBeenCalledTimes(2)
  expect(send.mock.lastCall?.[0]).toMatchObject({ value: 'abc' })
  saver.dispose()
})

test('a pause in typing collapses the edits to one save', async () => {
  const send = vi.fn<Send>(() => Promise.resolve('ok'))
  const { saver } = saverWith(send, 800)

  saver.edit('a')
  await vi.advanceTimersByTimeAsync(500)
  saver.edit('ab')
  await vi.advanceTimersByTimeAsync(500)
  expect(send).not.toHaveBeenCalled()

  await vi.advanceTimersByTimeAsync(300)

  expect(send).toHaveBeenCalledTimes(1)
  expect(send.mock.lastCall?.[0]).toMatchObject({ value: 'ab' })
  saver.dispose()
})

test('a failure with no reply goes again with the same time, and backs off', async () => {
  const send = vi.fn<Send>(() => Promise.reject(new Error('Network Error')))
  const { saver } = saverWith(send)

  saver.edit('a')
  await vi.advanceTimersByTimeAsync(0)
  expect(saver.getStatus()).toEqual({ kind: 'waiting' })

  await vi.advanceTimersByTimeAsync(2_000)
  expect(send).toHaveBeenCalledTimes(2)
  await vi.advanceTimersByTimeAsync(2_000)
  expect(send).toHaveBeenCalledTimes(2)
  await vi.advanceTimersByTimeAsync(2_000)
  expect(send).toHaveBeenCalledTimes(3)

  const times = send.mock.calls.map(([change]) => change.savedAt)
  expect(new Set(times).size).toBe(1)
  saver.dispose()
})

test('the connection coming back sends the waiting save at once', async () => {
  const send = vi.fn<Send>().mockRejectedValueOnce(new Error('Network Error')).mockResolvedValue('ok')
  const { saver, onReply } = saverWith(send)
  saver.edit('a')
  await vi.advanceTimersByTimeAsync(0)

  window.dispatchEvent(new Event('online'))
  await vi.advanceTimersByTimeAsync(0)

  expect(onReply).toHaveBeenCalledTimes(1)
  expect(saver.getStatus()).toMatchObject({ kind: 'saved' })
  saver.dispose()
})

test.each([
  [422, 'refused'],
  [404, 'refused'],
  [409, 'refused'],
  [401, 'waiting'],
  [408, 'waiting'],
  [429, 'waiting'],
  [500, 'waiting'],
  [503, 'waiting'],
])('a reply with status %i leaves the save %s', async (status, kind) => {
  const { saver } = saverWith(() => Promise.reject(refused(status, 'The reason.')))

  saver.edit('a')
  await vi.advanceTimersByTimeAsync(0)

  expect(saver.getStatus().kind).toBe(kind)
  saver.dispose()
})

test('a reply reaches the owner before the queue changes, so a copy of the queue has what the reply taught', async () => {
  const seen: string[] = []
  const saver = new SerialSaver<string, string>({
    debounceMs: 0,
    send: () => Promise.resolve('reply'),
    onReply: () => seen.push('reply'),
    onQueueChange: (queue) => {
      if (queue.lastSavedAt) seen.push('queue')
    },
  })

  saver.edit('a')
  await vi.advanceTimersByTimeAsync(0)

  expect(seen).toEqual(['reply', 'queue'])
  saver.dispose()
})

test.each([STALE_VERSION_PROBLEM_TYPE, CONCURRENT_CHANGE_PROBLEM_TYPE])('a 409 of type %s tries again', async (type) => {
  const { saver } = saverWith(() => Promise.reject(new ProblemDetailsError({ type, title: 'Changed', status: 409 })))

  saver.edit('a')
  await vi.advanceTimersByTimeAsync(0)

  expect(saver.getStatus().kind).toBe('waiting')
  saver.dispose()
})

test('a page can read the newest words that have no reply', async () => {
  const { saver } = saverWith(() => Promise.resolve('ok'), 800)

  saver.edit('a')
  expect(saver.newestUnsettledValue()).toBe('a')

  await vi.advanceTimersByTimeAsync(800)

  expect(saver.newestUnsettledValue()).toBeNull()
  saver.dispose()
})

test('a request that hangs ends at the limit, says it is waiting, and goes again', async () => {
  const signals: AbortSignal[] = []
  const send = vi.fn((_change: { value: string; savedAt: string }, signal: AbortSignal) => {
    signals.push(signal)
    return new Promise<string>((_resolve, reject) => {
      signal.addEventListener('abort', () => reject(new Error('Aborted')))
    })
  })
  const saver = new SerialSaver<string, string>({ debounceMs: 0, send, onReply: vi.fn() })

  saver.edit('a')
  await vi.advanceTimersByTimeAsync(29_000)
  expect(saver.getStatus().kind).toBe('saving')
  expect(signals[0].aborted).toBe(false)

  await vi.advanceTimersByTimeAsync(1_000)
  expect(signals[0].aborted).toBe(true)
  expect(saver.getStatus()).toEqual({ kind: 'waiting' })

  await vi.advanceTimersByTimeAsync(2_000)
  expect(send).toHaveBeenCalledTimes(2)
  saver.dispose()
})

test('a request that answers in time is not aborted later', async () => {
  const signals: AbortSignal[] = []
  const send = vi.fn((_change: { value: string; savedAt: string }, signal: AbortSignal) => {
    signals.push(signal)
    return Promise.resolve('ok')
  })
  const saver = new SerialSaver<string, string>({ debounceMs: 0, send, onReply: vi.fn() })

  saver.edit('a')
  await vi.advanceTimersByTimeAsync(60_000)

  expect(signals[0].aborted).toBe(false)
  saver.dispose()
})

test('settle sends a change that waits for the pause in typing, and answers true when the Server holds it', async () => {
  const send = vi.fn<Send>(() => Promise.resolve('ok'))
  const { saver } = saverWith(send, 800)

  saver.edit('a')
  const isSettled = saver.settle()
  await vi.advanceTimersByTimeAsync(0)

  await expect(isSettled).resolves.toBe(true)
  expect(send).toHaveBeenCalledTimes(1)
  saver.dispose()
})

test('settle answers false when the next try fails, and the words wait for a retry', async () => {
  const send = vi.fn<Send>(() => Promise.reject(new Error('Network Error')))
  const { saver } = saverWith(send)

  saver.edit('a')
  await vi.advanceTimersByTimeAsync(0)
  const isSettled = saver.settle()
  await vi.advanceTimersByTimeAsync(0)

  await expect(isSettled).resolves.toBe(false)
  expect(saver.newestUnsettledValue()).toBe('a')
  saver.dispose()
})

test('settle answers false for words that the Server refused', async () => {
  const send = vi.fn<Send>(() => Promise.reject(refused(422, 'Too long.')))
  const { saver } = saverWith(send)

  saver.edit('a')
  const isSettled = saver.settle()
  await vi.advanceTimersByTimeAsync(0)

  await expect(isSettled).resolves.toBe(false)
  expect(saver.refusalMessage()).toBe('Too long.')
  saver.dispose()
})
