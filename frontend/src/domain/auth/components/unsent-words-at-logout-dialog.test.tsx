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

  const list = screen.getByRole('list', { name: 'Words that did not reach the server' })
  await expect.element(list.getByText('first words')).toBeVisible()
  await expect.element(list.getByText('words of another tab')).toBeVisible()
  await screen.getByRole('button', { name: 'Discard words and sign out' }).click()

  ;(await finished)()
  expect(one.discard).toHaveBeenCalledOnce()
  expect(two.discard).toHaveBeenCalledOnce()
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
  await screen.getByRole('button', { name: 'Stay signed in' }).click()

  await expect(finished).rejects.toThrow()
})

test('a list of many long texts stays in its box, and the buttons stay in view', async () => {
  const { screen, finished } = await dialogFor(Array.from({ length: 30 }, (_, index) => words(`words ${index} `.repeat(40))))

  const list = screen.getByRole('list', { name: 'Words that did not reach the server' }).element()
  expect(list.clientHeight).toBeLessThan(200)
  await expect.element(screen.getByRole('button', { name: 'Keep words and sign out' })).toBeInViewport()

  await screen.getByRole('button', { name: 'Keep words and sign out' }).click()
  await finished
})
