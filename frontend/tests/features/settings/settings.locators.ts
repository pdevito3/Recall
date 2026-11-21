import type { Page } from '@playwright/test'

export const settings = (page: Page) => ({
  heading: page.getByRole('heading', { level: 1, name: 'Settings' }),
  zone: page.getByRole('combobox', { name: 'Time zone', exact: true }),
  zoneOption: (name: RegExp) => page.getByRole('option', { name }),
  zoneOptions: page.getByRole('option'),
  noZoneMatches: page.getByText('No time zone matches your search.', { exact: true }),
  zoneInUse: page.getByText(/^New entries use /),
  location: page.getByRole('checkbox', { name: 'Save my location with each new entry' }),
  proposals: page.getByRole('checkbox', { name: 'Ask me before AI adds a person to an entry' }),
  fillerWords: page.getByRole('checkbox', { name: 'Remove filler words, such as “um” and “uh”, from Cleaned' }),
  numbersAsDigits: page.getByRole('checkbox', { name: 'Write spoken numbers as digits, such as “3:30 PM” and “$20”, in Cleaned' }),
  signedInAs: (name: string) => page.getByText(`Signed in as ${name}`, { exact: true }),
  userFailed: page.getByText('The Server could not load your user.', { exact: true }),
  unknownZone: page.getByRole('alert').filter({ hasText: 'This server does not know your saved time zone' }),
  saved: page.getByRole('status').filter({ hasText: 'Saved at' }),
  passwordHeading: page.getByRole('heading', { level: 2, name: 'Password' }),
  currentPassword: page.getByLabel('Current password', { exact: true }),
  newPassword: page.getByLabel('New password', { exact: true }),
  repeatPassword: page.getByLabel('New password again', { exact: true }),
  passwordChanged: page.getByText('Password changed. Each other session and personal access token ended.', { exact: true }),
  message: (text: string) => page.getByText(text, { exact: true }),
})
