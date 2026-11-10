import { createMemoryHistory, createRootRoute, createRoute, createRouter, RouterProvider } from '@tanstack/react-router'
import { expect, onTestFinished, test, vi } from 'vitest'
import { page } from 'vitest/browser'
import { render } from 'vitest-browser-react'

import { EntryNotice } from './draft-notices'

function routerWith(component: () => React.ReactNode) {
  const rootRoute = createRootRoute({ component })
  const entryRoute = createRoute({ getParentRoute: () => rootRoute, path: '/sessions/$id' })
  return createRouter({ routeTree: rootRoute.addChildren([entryRoute]), history: createMemoryHistory({ initialEntries: ['/'] }) })
}

test('at 320 px with a font of 24 px the answers of a notice stay inside it, with no side scroll', async () => {
  await page.viewport(320, 640)
  document.documentElement.style.fontSize = '24px'
  onTestFinished(() => {
    document.documentElement.style.fontSize = ''
  })
  const router = routerWith(() => (
    <EntryNotice label="Words added to an entry" text="Words that you typed earlier and that were not saved yet are now in an entry." sessionId="ses_1" onDismiss={vi.fn()} />
  ))
  const screen = await render(<RouterProvider router={router} />)

  const notice = screen.getByRole('status', { name: 'Words added to an entry' })
  await expect.element(notice).toBeVisible()
  const edge = notice.element().getBoundingClientRect().right
  expect(screen.getByRole('button', { name: 'Dismiss' }).element().getBoundingClientRect().right).toBeLessThanOrEqual(edge)
  expect(screen.getByRole('link', { name: 'Open the entry' }).element().getBoundingClientRect().right).toBeLessThanOrEqual(edge)
  expect(document.documentElement.scrollWidth).toBeLessThanOrEqual(320)
})
