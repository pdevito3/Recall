import type { Page } from '@playwright/test'

export const corrections = (page: Page) => {
  const dialog = page.getByRole('dialog')
  return {
    heading: page.getByRole('heading', { level: 1, name: 'Corrections' }),
    search: page.getByRole('searchbox', { name: 'Search corrections' }),
    list: page.getByRole('list', { name: 'Your corrections' }),
    rows: page.getByRole('list', { name: 'Your corrections' }).getByRole('listitem'),
    term: (term: string) => page.getByRole('list', { name: 'Your corrections' }).getByText(term, { exact: true }),
    heardAs: (mishearings: string) => page.getByRole('list', { name: 'Your corrections' }).getByText(`Heard as ${mishearings}`, { exact: true }),
    alwaysReplaced: page.getByRole('list', { name: 'Your corrections' }).getByText('Always replaced', { exact: true }),
    empty: page.getByText('You have no corrections yet. Add a word that dictation gets wrong.', { exact: true }),
    noMatch: (search: string) => page.getByText(`No correction has “${search}” in its term or its mishearings.`, { exact: true }),
    add: page.getByRole('button', { name: 'Add correction', exact: true }),
    edit: (term: string) => page.getByRole('button', { name: `Edit ${term}`, exact: true }),
    remove: (term: string) => page.getByRole('button', { name: `Delete ${term}`, exact: true }),
    dialog,
    termField: dialog.getByRole('textbox', { name: 'Term' }),
    mishearingsField: dialog.getByRole('textbox', { name: 'Mishearings' }),
    alwaysReplace: dialog.getByRole('checkbox', { name: 'Always replace' }),
    save: dialog.getByRole('button', { name: 'Save correction', exact: true }),
    confirmDelete: dialog.getByRole('button', { name: 'Delete correction', exact: true }),
    termError: (message: string) => dialog.getByText(message, { exact: true }),
    toasts: page.getByRole('alertdialog'),
    toast: (text: string) => page.getByRole('alertdialog').filter({ hasText: text }),
  }
}
