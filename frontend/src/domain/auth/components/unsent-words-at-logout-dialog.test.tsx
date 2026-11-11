import { afterEach, expect, test, vi } from 'vitest'
import { render } from 'vitest-browser-react'

import { unsentWordsOnThisDevice, type UnsentWords } from '@/domain/sessions/autosave/draft-saver'
import { settleUnsentWords } from '../apis/unsent-words-at-logout'
import { UnsentWordsAtLogoutDialog } from './unsent-words-at-logout-dialog'

vi.mock('@/domain/sessions/autosave/draft-saver', async (original) => ({
  ...(await original<typeof import('@/domain/sessions/autosave/draft-saver')>()),
  settleDraftSavers: () => Promise.resolve(false),
  unsentWordsOnThisDevice: vi.fn(),
}))

const doc = (text: string) => JSON.stringify({ type: 'doc', content: [{ type: 'paragraph', content: [{ type: 'text', text }] }] })

const words = (text: string, more: Partial<UnsentWords> = {}): UnsentWords => ({ words: doc(text), isRefused: false, isOnlyInMemory: false, discard: vi.fn(), ...more })

async function dialogFor(unsent: UnsentWords[]) {
  vi.mocked(unsentWordsOnThisDevice).mockReturnValue(unsent)
  const screen = await render(<UnsentWordsAtLogoutDialog />)
  const finished = settleUnsentWords(() => undefined)
  await expect.element(screen.getByRole('alertdialog')).toBeInTheDocument()
  return { screen, finished }
}

afterEach(() => {
  vi.restoreAllMocks()
})

test('the dialog names each text, so a discard removes only words that the person saw', async () => {
  const one = words('first words')
  const two = words('words of another tab')
  const { screen, finished } = await dialogFor([one, two])

  const list = screen.getByRole('list', { name: 'Entries with words that did not reach the server' })
  await expect.element(list.getByText('first words')).toBeVisible()
  await expect.element(list.getByText('words of another tab')).toBeVisible()
  await screen.getByRole('button', { name: 'Discard words and sign out' }).click()

  ;(await finished)()
  expect(one.discard).toHaveBeenCalledOnce()
  expect(two.discard).toHaveBeenCalledOnce()
})

test('each text has its own Copy, so a person can take words before a sign-out', async () => {
  const copied: string[] = []
  vi.spyOn(navigator.clipboard, 'writeText').mockImplementation((text) => {
    copied.push(text)
    return Promise.resolve()
  })
  const { screen, finished } = await dialogFor([words('first words'), words('words of another tab')])

  const list = screen.getByRole('list', { name: 'Entries with words that did not reach the server' })
  await list.getByRole('button', { name: 'Copy words' }).nth(1).click()

  await vi.waitFor(() => expect(copied).toEqual(['words of another tab']))
  await screen.getByRole('button', { name: 'Keep words and sign out' }).click()
  ;(await finished)()
})

test('a keep does not remove a word', async () => {
  const one = words('first words')
  const { screen, finished } = await dialogFor([one])

  await screen.getByRole('button', { name: 'Keep words and sign out' }).click()

  ;(await finished)()
  expect(one.discard).not.toHaveBeenCalled()
})

test('a refused text is not said to go to the journal at a sign-in', async () => {
  const { screen, finished } = await dialogFor([words('too many words', { isRefused: true })])

  await expect.element(screen.getByText(/The server refused the words marked below, so they cannot go to your journal as they are\./)).toBeVisible()
  await expect.element(screen.getByText(/They go to your journal when you sign in here again\./)).not.toBeInTheDocument()

  await screen.getByRole('button', { name: 'Keep words and sign out' }).click()
  await finished
})

test('words that only a page holds cannot be kept, and the person can stay', async () => {
  const { screen, finished } = await dialogFor([words('words in memory', { isOnlyInMemory: true })])

  await expect.element(screen.getByRole('button', { name: 'Keep words and sign out' })).not.toBeInTheDocument()
  const stayed = expect(finished).rejects.toThrow()
  await screen.getByRole('button', { name: 'Stay signed in' }).click()

  await stayed
})

test('words that go to memory while the person reads the list are asked about again, with a cue, so a keep keeps each word', async () => {
  const { screen, finished } = await dialogFor([words('words of another tab')])
  const cue = screen.getByText(/The words changed after your answer, so check this new list\./)
  await expect.element(cue).not.toBeInTheDocument()
  vi.mocked(unsentWordsOnThisDevice).mockReturnValue([words('words of another tab', { isOnlyInMemory: true })])
  const stayed = expect(finished).rejects.toThrow()

  await screen.getByRole('button', { name: 'Keep words and sign out' }).click()

  await expect.element(cue).toBeVisible()
  await expect.element(screen.getByRole('button', { name: 'Stay signed in' })).toBeVisible()
  await expect.element(screen.getByRole('button', { name: 'Keep words and sign out' })).not.toBeInTheDocument()
  await screen.getByRole('button', { name: 'Stay signed in' }).click()
  await stayed
})

test('a list of many long texts stays in its box, and the buttons stay in view', async () => {
  const { screen, finished } = await dialogFor(Array.from({ length: 30 }, (_, index) => words(`words ${index} `.repeat(40))))

  const list = screen.getByRole('list', { name: 'Entries with words that did not reach the server' }).element()
  expect(list.clientHeight).toBeLessThan(200)
  await expect.element(screen.getByRole('button', { name: 'Keep words and sign out' })).toBeInViewport()

  await screen.getByRole('button', { name: 'Keep words and sign out' }).click()
  await finished
})
