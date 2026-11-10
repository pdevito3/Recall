import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { expect, test, vi } from 'vitest'
import { page } from 'vitest/browser'
import { render } from 'vitest-browser-react'

import { Sidebar, SidebarProvider } from '@/components/ui/sidebar'
import { useSidebar } from '@/components/ui/use-sidebar'

import { UserMenu } from './user-menu'

const mutate = vi.fn()

vi.mock('../apis/auth-actions', () => ({ useLogout: () => ({ mutate }) }))
vi.mock('../apis/get-user', () => ({ useAuth: () => ({ username: 'Ann' }) }))

function OpenDrawer() {
  const { setOpenMobile } = useSidebar()
  return <button onClick={() => setOpenMobile(true)}>Open drawer</button>
}

test('on a phone a sign-out closes the drawer, so its toast does not lie over the drawer', async () => {
  await page.viewport(390, 844)
  await render(
    <QueryClientProvider client={new QueryClient()}>
      <SidebarProvider>
        <OpenDrawer />
        <Sidebar>
          <UserMenu />
        </Sidebar>
      </SidebarProvider>
    </QueryClientProvider>,
  )
  await page.getByRole('button', { name: 'Open drawer' }).click()

  await page.getByRole('button', { name: 'User menu for Ann' }).click()
  await page.getByRole('menuitem', { name: 'Logout' }).click()

  expect(mutate).toHaveBeenCalledOnce()
  await expect.element(page.getByRole('button', { name: 'User menu for Ann' })).not.toBeInTheDocument()
})
