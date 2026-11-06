import { afterEach, expect, test, vi } from 'vitest'
import { render } from 'vitest-browser-react'

import { httpsGuideUrl } from '../https-guide'
import { InsecureContextNotice } from './insecure-context-notice'

afterEach(() => {
  vi.unstubAllGlobals()
})

test('a page on plain HTTP says that the mic stays off and links to the HTTPS section', async () => {
  vi.stubGlobal('isSecureContext', false)

  const screen = await render(<InsecureContextNotice />)

  await expect.element(screen.getByRole('region', { name: 'Mic' })).toHaveTextContent('The mic needs HTTPS. This page opened on plain HTTP, so the mic stays off. Turn on HTTPS')
  await expect.element(screen.getByRole('link', { name: 'Turn on HTTPS' })).toHaveAttribute('href', httpsGuideUrl)
})

test('a page on HTTPS shows no message', async () => {
  vi.stubGlobal('isSecureContext', true)

  const screen = await render(<InsecureContextNotice />)

  await expect.element(screen.getByRole('region', { name: 'Mic' })).not.toBeInTheDocument()
})
