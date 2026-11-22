import type { Page } from '@playwright/test'

export const loginProviders = (page: Page) => {
  const card = (name: string) => page.getByRole('article', { name, exact: true })
  const dialog = page.getByRole('dialog')
  const passwordLogin = page.getByRole('region', { name: 'Password login' })
  const turnOffAsk = page.getByRole('dialog', { name: 'Turn password login off?' })

  return {
    heading: page.getByRole('heading', { level: 1, name: 'Login providers' }),
    listHeading: page.getByRole('heading', { level: 2, name: 'Providers' }),
    settingsLink: page.getByRole('navigation', { name: 'Main' }).getByRole('link', { name: 'Settings' }),
    settingsHeading: page.getByRole('heading', { level: 1, name: 'Settings' }),
    passwordHeading: page.getByRole('heading', { level: 2, name: 'Password' }),
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
    passwordLogin,
    onlyClaimOwnerNote: page.getByText(/only the claim-owner file of the server lets anyone in/),
    passwordLoginState: (state: 'On' | 'Off') => passwordLogin.getByText(state, { exact: true }),
    passwordLoginText: (text: string | RegExp) => passwordLogin.getByText(text),
    passwordLoginAlert: passwordLogin.getByRole('alert'),
    passwordLoginHeading: page.getByRole('heading', { level: 2, name: 'Password login' }),
    turnOff: passwordLogin.getByRole('button', { name: 'Turn password login off' }),
    turnOn: passwordLogin.getByRole('button', { name: 'Turn password login on' }),
    turnOffAsk,
    turnOffAskText: (text: string | RegExp) => turnOffAsk.getByText(text),
    confirmTurnOff: turnOffAsk.getByRole('button', { name: 'Turn password login off' }),
    keepOn: turnOffAsk.getByRole('button', { name: 'Keep password login on' }),
    toast: (text: string) => page.getByRole('alertdialog').filter({ hasText: text }),
    pageOverflow: () => page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth),
    controls: page.locator('main').getByRole('button').or(page.locator('main input:not([type=hidden])')),
  }
}
