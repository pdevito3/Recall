import { readFileSync } from 'node:fs'
import { expect, test } from 'vitest'

import { httpsGuideUrl } from './https-guide'

test('the link of the mic message opens a section that the install doc has', () => {
  const installDoc = readFileSync(new URL('../../../../docs/install.md', import.meta.url), 'utf8')
  const anchors = [...installDoc.matchAll(/^#+ (.+)$/gm)].map(([, title]) => title.toLowerCase().replaceAll(' ', '-'))

  expect(anchors).toContain(new URL(httpsGuideUrl).hash.slice(1))
})
