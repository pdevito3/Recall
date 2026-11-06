import { expect, test } from 'vitest'

import { emptyQueue, newestUnsettled, nextToSend, queueWithUnanswered, statusOf, step, type SaveEvent, type SaveQueue } from './save-queue'

const edited = (value: string, savedAt: string): SaveEvent<string> => ({ type: 'edited', change: { value, savedAt } })
const run = (...events: SaveEvent<string>[]) =>
  events.reduce<SaveQueue<string>>((queue, event) => step(queue, event), emptyQueue())

test('a new row has nothing to send and no status to show', () => {
  const queue = emptyQueue<string>()

  expect(nextToSend(queue)).toBeNull()
  expect(statusOf(queue)).toEqual({ kind: 'idle' })
})

test('an edit is unsent, then saving, then saved at the time of the reply', () => {
  const unsent = run(edited('a', 't1'))
  expect(statusOf(unsent)).toEqual({ kind: 'unsent' })

  const sending = step(unsent, { type: 'sent' })
  expect(statusOf(sending)).toEqual({ kind: 'saving' })

  expect(statusOf(step(sending, { type: 'replied', savedAt: 't1' }))).toEqual({ kind: 'saved', at: 't1' })
})

test('only one save is on the wire, and edits in that time collapse to the newest', () => {
  const queue = run(edited('a', 't1'), { type: 'sent' }, edited('ab', 't2'), edited('abc', 't3'))

  expect(nextToSend(queue)).toBeNull()

  const replied = step(queue, { type: 'replied', savedAt: 't1' })
  expect(nextToSend(replied)).toEqual({ value: 'abc', savedAt: 't3' })
})

test('a save with no reply goes again with its own time before a newer change', () => {
  const queue = run(edited('a', 't1'), { type: 'sent' }, edited('ab', 't2'), { type: 'noReply' })

  expect(statusOf(queue)).toEqual({ kind: 'waiting' })
  expect(nextToSend(queue)).toEqual({ value: 'a', savedAt: 't1' })

  const replayed = step(step(queue, { type: 'sent' }), { type: 'replied', savedAt: 't1' })
  expect(nextToSend(replayed)).toEqual({ value: 'ab', savedAt: 't2' })
})

test('a retry stays in the waiting state until a reply comes, so the state does not flip on the screen', () => {
  const retrying = run(edited('a', 't1'), { type: 'sent' }, { type: 'noReply' }, { type: 'sent' })

  expect(statusOf(retrying)).toEqual({ kind: 'waiting' })
  expect(statusOf(step(retrying, { type: 'replied', savedAt: 't1' }))).toEqual({ kind: 'saved', at: 't1' })
})

test('a refusal shows until the next edit, and the refused change does not go again', () => {
  const refused = run(edited('a', 't1'), { type: 'sent' }, { type: 'refused', message: 'Too long.', isGone: false })

  expect(statusOf(refused)).toEqual({ kind: 'refused', message: 'Too long.', isGone: false })
  expect(nextToSend(refused)).toBeNull()
  expect(statusOf(step(refused, edited('b', 't2')))).toEqual({ kind: 'unsent' })
})

test('refused words stay unsettled until the next edit, so a reload still has them', () => {
  const refused = run(edited('a', 't1'), { type: 'sent' }, { type: 'refused', message: 'Too long.', isGone: false })

  expect(newestUnsettled(refused)?.value).toBe('a')
  expect(newestUnsettled(step(refused, edited('b', 't2')))?.value).toBe('b')
})

test('words of a row that is gone do not stay', () => {
  const gone = run(edited('a', 't1'), { type: 'sent' }, { type: 'refused', message: 'Not found.', isGone: true })

  expect(newestUnsettled(gone)).toBeNull()
})

test('a queue from an earlier page load replays its change first', () => {
  const queue = queueWithUnanswered({ value: 'a', savedAt: 't1' })

  expect(statusOf(queue)).toEqual({ kind: 'waiting' })
  expect(nextToSend(queue)).toEqual({ value: 'a', savedAt: 't1' })
})

test('the newest unsettled change is the newest words that have no reply', () => {
  expect(newestUnsettled(emptyQueue<string>())).toBeNull()
  expect(newestUnsettled(run(edited('a', 't1'), { type: 'sent' }))?.value).toBe('a')
  expect(newestUnsettled(run(edited('a', 't1'), { type: 'sent' }, edited('ab', 't2')))?.value).toBe('ab')
  expect(newestUnsettled(run(edited('a', 't1'), { type: 'sent' }, { type: 'noReply' }))?.value).toBe('a')
  expect(newestUnsettled(run(edited('a', 't1'), { type: 'sent' }, { type: 'replied', savedAt: 't1' }))).toBeNull()
})
