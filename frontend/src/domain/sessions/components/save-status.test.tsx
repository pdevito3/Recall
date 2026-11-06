import { expect, test } from 'vitest'
import { render } from 'vitest-browser-react'

import type { TimeZone } from '@/lib/zoned-time'

import type { ShownSaveStatus } from '../autosave/draft-saver'
import { SaveStatusLine } from './save-status'

const chicago = 'America/Chicago' as TimeZone

test.each<[ShownSaveStatus, string]>([
  [{ kind: 'unsent' }, 'Not saved yet'],
  [{ kind: 'saving' }, 'Saving'],
  [{ kind: 'waiting' }, 'Not saved yet. The save waits for a connection to the server. Keep this page open.'],
  [{ kind: 'saved', at: '2026-11-06T15:30:00Z' }, 'Saved at 9:30 AM'],
  [{ kind: 'savedInHistory', at: '2026-11-06T15:30:00Z' }, 'Saved in History at 9:30 AM'],
  [{ kind: 'refused', message: 'This text is too long.' }, 'Not saved. This text is too long.'],
])('the state %j says what happened', async (status, text) => {
  const screen = await render(<SaveStatusLine status={status} zone={chicago} />)

  await expect.element(screen.getByRole('status')).toHaveTextContent(text)
})

test('with no edit, the line is there and empty, so a first save does not move the page', async () => {
  const screen = await render(<SaveStatusLine status={{ kind: 'idle' }} zone={chicago} />)

  await expect.element(screen.getByRole('status')).toHaveTextContent('')
})
