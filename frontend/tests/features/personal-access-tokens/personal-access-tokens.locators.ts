import type { Page } from '@playwright/test'

export const personalAccessTokens = (page: Page) => {
  const dialog = page.getByRole('dialog')
  const mine = page.getByRole('list', { name: 'Your personal access tokens' })
  return {
    heading: page.getByRole('heading', { level: 1, name: 'Personal access tokens' }),
    navLink: page.getByRole('navigation', { name: 'Main' }).getByRole('link', { name: 'Personal access tokens', exact: true }),
    settingsLink: page.getByRole('link', { name: 'Manage personal access tokens', exact: true }),
    settingsNavLink: page.getByRole('navigation', { name: 'Main' }).getByRole('link', { name: 'Settings', exact: true }),
    aboutHeading: page.getByRole('heading', { level: 1, name: 'About' }),
    list: mine,
    row: (name: string) => mine.getByRole('listitem', { name, exact: true }),
    empty: page.getByText('You have no personal access tokens yet. Make one for each client that you use.', { exact: true }),
    loadFailure: page.getByRole('alert').filter({ hasText: 'The tokens did not load.' }),
    newToken: page.getByRole('button', { name: 'New token', exact: true }),
    revoke: (name: string) => page.getByRole('button', { name: `Revoke ${name}`, exact: true }),
    dialog,
    nameField: dialog.getByRole('textbox', { name: 'Name' }),
    readJournal: dialog.getByRole('checkbox', { name: 'Read my journal' }),
    changeJournal: dialog.getByRole('checkbox', { name: 'Change my journal' }),
    permissionChoices: dialog.getByRole('group', { name: 'The token can also' }).getByRole('checkbox'),
    permission: (words: string) => dialog.getByRole('checkbox', { name: words, exact: true }),
    expiry: dialog.getByRole('button', { name: /Expires$/ }),
    expiryOption: (label: string | RegExp) => page.getByRole('option', { name: label }),
    lastDayMonth: dialog.getByRole('spinbutton', { name: 'month, Last day', exact: true }),
    fieldError: (message: string) => dialog.getByText(message, { exact: true }),
    refusal: dialog.getByRole('alert'),
    create: dialog.getByRole('button', { name: 'Create token', exact: true }),
    createdTitle: dialog.getByRole('heading', { name: 'Copy your new token' }),
    value: (value: string) => dialog.getByText(value, { exact: true }),
    copy: dialog.getByRole('button', { name: 'Copy token', exact: true }),
    done: dialog.getByRole('button', { name: 'Done', exact: true }),
    closeDialog: dialog.getByRole('button', { name: 'Close', exact: true }),
    cancel: dialog.getByRole('button', { name: 'Cancel', exact: true }),
    leaveQuestion: page.getByRole('alertdialog', { name: 'Leave without your token?' }),
    stay: page.getByRole('alertdialog', { name: 'Leave without your token?' }).getByRole('button', { name: 'Stay', exact: true }),
    leave: page.getByRole('alertdialog', { name: 'Leave without your token?' }).getByRole('button', { name: 'Leave', exact: true }),
    confirmRevoke: dialog.getByRole('button', { name: 'Revoke token', exact: true }),
    anyTokenValue: page.getByText(/rcl_pat_/),
    toast: (text: string) => page.getByRole('alertdialog').filter({ hasText: text }),
    toasts: page.getByRole('alertdialog'),
    userSection: (fullName: string) => page.getByRole('list', { name: `Personal access tokens of ${fullName}` }),
    userRow: (fullName: string, name: string) =>
      page.getByRole('list', { name: `Personal access tokens of ${fullName}` }).getByRole('listitem', { name, exact: true }),
    userEmpty: (fullName: string) => page.getByText(`${fullName} has no personal access tokens.`, { exact: true }),
    userHeading: (fullName: string) => page.getByRole('heading', { level: 1, name: fullName, exact: true }),
  }
}
