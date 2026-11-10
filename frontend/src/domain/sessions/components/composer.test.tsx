import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { expect, onTestFinished, test, vi } from 'vitest'
import { page, userEvent } from 'vitest/browser'
import { render } from 'vitest-browser-react'

import { PersonKeys } from '@/domain/people/apis/person.keys'
import { SettingKeys } from '@/domain/settings/apis/setting.keys'

import type { ShownSaveStatus } from '../autosave/draft-saver'
import { ComposerForm } from './composer'
import { mergedNote } from './conflict-notice'
import type { Composer } from './use-composer'

type ShownComposer = Partial<Pick<Composer, 'status' | 'isMergeNoted' | 'editorKey' | 'words'>>

const composerWith = (shown: ShownComposer): Composer => ({
  editorKey: 0,
  words: '',
  autoFocus: false,
  status: { kind: 'idle' },
  conflict: null,
  composingSessionId: null,
  canFinish: true,
  handleChange: () => undefined,
  finish: () => undefined,
  keepWords: () => undefined,
  isMergeNoted: false,
  dismissMergeNote: () => undefined,
  showNewest: () => undefined,
  editing: 'here',
  hasEdited: true,
  wordsOfTheEditor: null,
  editHere: () => undefined,
  ...shown,
})

const statuses: ShownSaveStatus[] = [
  { kind: 'idle' },
  { kind: 'unsent' },
  { kind: 'saving' },
  { kind: 'waiting' },
  { kind: 'saved', at: '2026-11-08T09:30:00Z' },
  { kind: 'waitingForAnotherWriter' },
  { kind: 'savedInHistory', at: '2026-11-08T09:30:00Z' },
  { kind: 'refused', message: 'The entry is too long. Make it shorter, then save again.' },
]

const renderComposer = async (first: ShownComposer) => {
  await page.viewport(320, 640)
  document.documentElement.style.fontSize = '24px'
  onTestFinished(() => {
    document.documentElement.style.fontSize = ''
  })
  const client = new QueryClient({ defaultOptions: { queries: { staleTime: Infinity } } })
  client.setQueryData(SettingKeys.mine(), { timeZone: 'UTC', timeZoneIsKnown: true, locationCaptureEnabled: false, requirePeopleProposalApproval: false, version: 1 })
  client.setQueryData(PersonKeys.directory(), [])
  const shown = (current: ShownComposer) => (
    <QueryClientProvider client={client}>
      <ComposerForm composer={composerWith(current)} />
    </QueryClientProvider>
  )
  const screen = await render(shown(first))
  return { screen, show: (next: ShownComposer) => screen.rerender(shown({ ...first, ...next })) }
}

const typeLinesUpToTheBar = async () => {
  await page.getByRole('textbox', { name: 'New entry' }).click()
  for (let line = 1; line <= 16; line++) await userEvent.keyboard(`line ${line} of the words{Enter}`)
  await userEvent.keyboard('the last line')
}

const caretLine = () => {
  const node = window.getSelection()?.focusNode
  const element = node instanceof Element ? node : node?.parentElement
  return element?.closest('p')?.getBoundingClientRect()
}

/** The top of the status line or of Done, whichever is higher. The bar sticks to the bottom of the screen, so this is where it starts to cover the words. */
const barTop = () => {
  const status = page.getByRole('status').element().getBoundingClientRect()
  const done = page.getByRole('button', { name: 'Done' }).element().getBoundingClientRect()
  return Math.min(status.top, done.top)
}

const caretIsAboveTheBar = () => {
  const line = caretLine()
  expect(line, 'the caret is in a line').toBeDefined()
  expect(line?.bottom, 'the caret line ends above the bar').toBeLessThanOrEqual(barTop())
  expect(line?.top, 'the caret line is on the screen').toBeGreaterThanOrEqual(0)
}

test.each(statuses)('at 320 px with a font of 24 px the line with the caret stays above the bar while the person types, with the status $kind', async (status) => {
  await renderComposer({ status })

  await page.getByRole('textbox', { name: 'New entry' }).click()
  for (let line = 1; line <= 16; line++) {
    await userEvent.keyboard(`line ${line} of the words{Enter}`)
    caretIsAboveTheBar()
  }
  await userEvent.keyboard('the last line')
  caretIsAboveTheBar()
})

test.each(statuses.filter(({ kind }) => kind !== 'idle'))('at 320 px with a font of 24 px a status $kind that makes the bar taller keeps the line with the caret above the bar', async (status) => {
  const { show } = await renderComposer({ status: { kind: 'idle' } })

  await typeLinesUpToTheBar()
  caretIsAboveTheBar()
  await show({ status })

  await vi.waitFor(caretIsAboveTheBar)
})

test('at 320 px with a font of 24 px a merge that adds lines while the person writes keeps the line with the caret above the bar', async () => {
  const { show } = await renderComposer({ status: { kind: 'waitingForAnotherWriter' } })
  const merged = JSON.stringify({
    type: 'doc',
    content: Array.from({ length: 24 }, (_, line) => ({ type: 'paragraph', content: [{ type: 'text', text: `line ${line + 1} of the merged words` }] })),
  })

  await typeLinesUpToTheBar()
  await show({ editorKey: 1, words: merged })

  await expect.element(page.getByText('line 24 of the merged words')).toBeInTheDocument()
  caretIsAboveTheBar()
})

test('while the merged words wait to save, the page does not say that they are in the newest words, and says it once they save', async () => {
  const { show } = await renderComposer({ status: { kind: 'waitingForAnotherWriter' }, isMergeNoted: true })

  await expect.element(page.getByText('Another tab or device keeps changing this entry. Your words are safe, and they wait to save.')).toBeVisible()
  expect(page.getByText(mergedNote).query()).toBeNull()
  await show({ status: { kind: 'saved', at: '2026-11-08T09:30:00Z' } })

  await expect.element(page.getByText(mergedNote)).toBeVisible()
})
