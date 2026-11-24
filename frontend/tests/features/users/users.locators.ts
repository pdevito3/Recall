import type { Page } from '@playwright/test'

export const users = (page: Page) => {
  const grid = page.getByRole('grid', { name: 'Users' })
  const roleDialog = page.getByRole('dialog', { name: /^Change the role of / })
  const deleteDialog = page.getByRole('dialog', { name: /^Delete .+\?$/ })
  const details = page.getByRole('region', { name: 'Details' })
  const temporaryPasswordAsk = page.getByRole('dialog', { name: /^Set a temporary password for .+\?$/ })
  const temporaryPasswordResult = page.getByRole('dialog', { name: /^Give .+ this password$/ })
  const leaveQuestion = page.getByRole('alertdialog', { name: 'Leave without the password?' })

  return {
    listHeading: page.getByRole('heading', { name: 'Users', exact: true }),
    journalNavLink: page.getByRole('navigation', { name: 'Main' }).getByRole('link', { name: 'Journal', exact: true }),
    usersNavLink: page.getByRole('navigation', { name: 'Main' }).getByRole('link', { name: 'Users', exact: true }),
    grid,
    row: (name: string) => grid.getByRole('row', { name: new RegExp(`^${name}`) }),
    cell: (name: string, text: string) =>
      grid.getByRole('row', { name: new RegExp(`^${name}`) }).getByRole('gridcell', { name: text, exact: true }),
    nameLink: (name: string) => grid.getByRole('link', { name, exact: true }),
    nextPage: page.getByRole('navigation', { name: 'Pages of users' }).getByRole('button', { name: 'Next' }),
    nameLinks: grid.getByRole('link'),
    pagerButtons: page.getByRole('navigation', { name: 'Pages of users' }).getByRole('button'),
    usersBreadcrumbLink: page.getByRole('navigation', { name: 'breadcrumb' }).getByRole('link', { name: 'Users', exact: true }),
    emptyPage: page.getByText(/^There are no users on page \d+\./),
    firstPageLink: page.getByRole('link', { name: 'Go to page 1' }),
    loadFailure: page.getByRole('alert').filter({ hasText: 'The users did not load.' }),
    signInMethods: (owner: string) => page.getByRole('list', { name: `Sign-in methods of ${owner}` }),
    signInMethod: (owner: string, name: string) =>
      page.getByRole('list', { name: `Sign-in methods of ${owner}` }).getByRole('listitem', { name, exact: true }),
    signInMethodsHeading: page.getByRole('heading', { name: 'Sign-in methods' }),
    signInMethodRemoved: page.getByText('Sign-in method removed'),
    detailHeading: (name: string) => page.getByRole('heading', { name, exact: true }),
    detail: (text: string) => details.getByRole('definition').and(details.getByText(text, { exact: true })),
    actions: (name: string) => page.getByRole('group', { name: `Actions for ${name}` }),
    actionButtons: (name: string) => page.getByRole('group', { name: `Actions for ${name}` }).getByRole('button'),
    actionButton: (name: string, label: string) =>
      page.getByRole('group', { name: `Actions for ${name}` }).getByRole('button', { name: label, exact: true }),
    roleDialog,
    roleSelect: roleDialog.getByRole('button', { name: / Role$/ }),
    roleOption: (label: string) => page.getByRole('option', { name: label, exact: true }),
    keepRole: roleDialog.getByRole('button', { name: 'Keep role', exact: true }),
    confirmRole: roleDialog.getByRole('button', { name: 'Change role', exact: true }),
    deleteDialog,
    keepUser: deleteDialog.getByRole('button', { name: 'Keep user', exact: true }),
    confirmDelete: deleteDialog.getByRole('button', { name: 'Delete user', exact: true }),
    temporaryPasswordAsk,
    loginNameField: temporaryPasswordAsk.getByRole('textbox', { name: 'Login name' }),
    cancelTemporaryPassword: temporaryPasswordAsk.getByRole('button', { name: 'Cancel', exact: true }),
    temporaryPasswordRefusal: temporaryPasswordAsk.getByRole('alert'),
    confirmTemporaryPassword: temporaryPasswordAsk.getByRole('button', { name: 'Set temporary password', exact: true }),
    temporaryPasswordResult,
    copyPassword: temporaryPasswordResult.getByRole('button', { name: 'Copy password', exact: true }),
    donePassword: temporaryPasswordResult.getByRole('button', { name: 'Done', exact: true }),
    leaveQuestion,
    stay: leaveQuestion.getByRole('button', { name: 'Stay', exact: true }),
    leave: leaveQuestion.getByRole('button', { name: 'Leave', exact: true }),
    toast: (text: string) => page.getByRole('alertdialog').filter({ hasText: text }),
    toasts: page.getByRole('alertdialog'),
  }
}
