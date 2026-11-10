import type { Page } from '@playwright/test'

export const people = (page: Page) => ({
  heading: page.getByRole('heading', { level: 1, name: 'People' }),
  newPerson: page.getByRole('textbox', { name: 'Add a person' }),
  add: page.getByRole('button', { name: 'Add', exact: true }),
  person: (name: string) => page.getByRole('list', { name: 'Your people' }).getByText(name, { exact: true }),
  empty: page.getByText('You have no people yet.', { exact: true }),
  rename: (name: string) => page.getByRole('button', { name: `Rename ${name}`, exact: true }),
  renameField: page.getByRole('dialog').getByRole('textbox', { name: 'Name' }),
  saveName: page.getByRole('button', { name: 'Save name', exact: true }),
})
