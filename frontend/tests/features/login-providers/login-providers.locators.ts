import type { Page } from '@playwright/test'

export const loginProviders = (page: Page) => {
  const card = (name: string) => page.getByRole('article', { name, exact: true })
  const dialog = page.getByRole('dialog')

  return {
    heading: page.getByRole('heading', { level: 1, name: 'Login providers' }),
    listHeading: page.getByRole('heading', { level: 2, name: 'Providers' }),
    navLink: page.getByRole('navigation', { name: 'Main' }).getByRole('link', { name: 'Login providers' }),
    noAdminNote: page.getByText('An Admin sets up the login providers of this install.'),
    callbackUrl: (url: string) => page.getByText(url, { exact: true }),
    copyCallbackUrl: page.getByRole('button', { name: 'Copy callback URL' }),
    card,
    cardText: (name: string, text: string) => card(name).getByText(text),
    edit: (name: string) => card(name).getByRole('button', { name: 'Edit' }),
    delete: (name: string) => card(name).getByRole('button', { name: 'Delete' }),
    cardAlert: (name: string) => card(name).getByRole('alert'),
    add: page.getByRole('button', { name: 'Add a login provider' }),
    dialog,
    idField: dialog.getByLabel('ID', { exact: true }),
    nameField: dialog.getByLabel('Name', { exact: true }),
    issuerField: dialog.getByLabel('Issuer URL'),
    clientIdField: dialog.getByLabel('Client ID'),
    clientSecretField: dialog.getByLabel('Client secret'),
    allowHttp: dialog.getByRole('checkbox', { name: 'Allow HTTP' }),
    caPathField: dialog.getByLabel('CA file path'),
    testIssuer: dialog.getByRole('button', { name: 'Test the issuer' }),
    testPassed: dialog.getByRole('status').filter({ hasText: 'The provider answered.' }),
    testFailed: dialog.getByRole('alert').filter({ hasText: 'The test failed.' }),
    saveNew: dialog.getByRole('button', { name: 'Add login provider' }),
    saveEdit: dialog.getByRole('button', { name: 'Save login provider' }),
    loadLatest: dialog.getByRole('button', { name: 'Load the latest' }),
    latestFailed: dialog.getByRole('alert').filter({ hasText: 'The latest login provider did not load.' }),
    staleAlert: dialog.getByRole('alert').filter({ hasText: 'This login provider changed after you opened it.' }),
    confirmDelete: page.getByRole('button', { name: 'Delete login provider' }),
    dialogText: (text: string) => dialog.getByText(text),
    toast: (text: string) => page.getByRole('alertdialog').filter({ hasText: text }),
    pageOverflow: () => page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth),
    controls: page.locator('main').getByRole('button').or(page.locator('main input:not([type=hidden])')),
  }
}
