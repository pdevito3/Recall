import type { Page } from '@playwright/test'

export const home = (page: Page) => ({
  heading: page.getByRole('main').getByRole('heading', { level: 1 }),
  journalComposer: page.getByRole('textbox', { name: 'New entry' }),
  userMenu: (name: string) => page.getByRole('button', { name: `User menu for ${name}`, exact: true }),
  userMenuName: (name: string) => page.getByRole('menu', { name: 'User menu' }).getByText(name, { exact: true }),
  userMenuLogout: page.getByRole('menuitem', { name: 'Logout', exact: true }),
  micCard: page.getByRole('region', { name: 'Record an entry' }),
  record: page.getByRole('region', { name: 'Record an entry' }).getByRole('button', { name: 'Record', exact: true }),
  micMessage: page.getByRole('region', { name: 'Record an entry' }).getByRole('status').getByText('The mic needs HTTPS', { exact: true }),
  httpsGuide: page.getByRole('link', { name: 'Turn on HTTPS', exact: true }),
  logoutFailed: page.getByText(/^You are still signed in, because .+ did not finish\. Try again\.$/),
})
