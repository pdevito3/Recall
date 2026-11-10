import type { Page } from '@playwright/test'

export const settings = (page: Page) => ({
  heading: page.getByRole('heading', { level: 1, name: 'Settings' }),
  zone: page.getByRole('combobox', { name: 'Time zone' }),
  location: page.getByRole('checkbox', { name: 'Save my location with each new entry' }),
  proposals: page.getByRole('checkbox', { name: 'Ask me before AI adds a person to an entry' }),
  signedInAs: (name: string) => page.getByText(`Signed in as ${name}`, { exact: true }),
  userFailed: page.getByText('The Server could not load your user.', { exact: true }),
  unknownZone: page.getByRole('alert').filter({ hasText: 'This server does not know your saved time zone' }),
  saved: page.getByRole('status').filter({ hasText: 'Saved at' }),
})
