import { expect, it } from 'vitest'

import { momentOf } from './import-words'

it('@IMP-33 a moment has no space where a line can break, so its date and its time wrap as one unit', () => {
  const moment = momentOf('2026-11-26T21:14:00Z')

  expect(moment).toMatch(/2026/)
  expect(moment).not.toContain(' ')
})
