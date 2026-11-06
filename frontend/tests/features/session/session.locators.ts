import type { Page } from '@playwright/test'

export const session = (page: Page) => ({
  checkFailed: page.getByRole('alert').filter({ hasText: 'We could not check whether you are signed in.' }),
  tryAgain: page.getByRole('button', { name: 'Try Again', exact: true }),
  navigation: page.getByRole('navigation', { name: 'Main' }),
  settingsLink: page.getByRole('navigation', { name: 'Main' }).getByRole('link', { name: 'Settings', exact: true }),
  aboutHeading: page.getByRole('heading', { level: 1, name: 'About' }),
  userMenu: page.getByRole('button', { name: /^User menu for/ }),
  logout: page.getByRole('menuitem', { name: 'Logout', exact: true }),
  toasts: page.getByRole('alertdialog'),
  rejectedSignInToast: page.getByRole('alertdialog').filter({ hasText: 'The Server did not accept your sign-in for this request.' }),
})
