export default async ({ page, step, capture, settle, expect, login }) => {
  const stamp = Date.now().toString(36)
  const answered = (method, pattern) =>
    page.waitForResponse((r) => r.request().method() === method && pattern.test(new URL(r.url()).pathname))
  const usersTable = () => page.getByRole('grid', { name: 'Users' }).or(page.getByRole('table', { name: 'Users' }))

  const me = await step('log in as the Owner, an Admin of the tenant of a new database', async () => {
    const session = await login()
    const body = await page.evaluate(async () => {
      const headers = { 'X-CSRF': '1' }
      const current = await (await fetch('/api/v1/users/me', { headers })).json()
      return (await fetch(`/api/v1/users/by-identifier/${encodeURIComponent(current.identifier)}`, { headers })).json()
    })
    return { ...body, name: session.name }
  })

  const colleague = await step('add a colleague to the tenant through the API', async () => {
    const res = await page.evaluate(async (s) => {
      const me = await (await fetch('/auth/me', { headers: { 'X-CSRF': '1' } })).json()
      const r = await fetch('/api/v1/users', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', 'X-CSRF': '1', 'X-Recall-User': me.sub },
        body: JSON.stringify({
          firstName: 'Casey',
          lastName: `Colleague ${s}`,
          email: `casey-${s}@example.com`,
          username: `casey-${s}`,
          roles: ['Auditor'],
        }),
      })
      return { status: r.status, body: await r.json() }
    }, stamp)
    expect(res.status).toBe(201)
    return res.body
  })

  await step('Users in the sidebar: the list shows each user of the tenant with the values of the API', async () => {
    const list = answered('GET', /^\/api\/v1\/users$/)
    await page.goto('/')
    await page.getByRole('link', { name: 'Users', exact: true }).click()
    const res = await list
    expect(res.status()).toBe(200)
    const rows = await res.json()
    await expect(page.getByRole('heading', { name: 'Users', exact: true })).toBeVisible()
    for (const user of [me, colleague]) {
      const row = rows.find((r) => r.id === user.id)
      expect(row).toBeTruthy()
      const shown = usersTable().getByRole('row').filter({ has: page.getByRole('link', { name: row.fullName, exact: true }) })
      await expect(shown).toContainText(row.email)
      await expect(shown).toContainText(row.roles.length === 0 ? 'Member' : row.roles.includes('Admin') ? 'Admin' : 'Auditor')
    }
  })
  await capture('list')

  await step('open the colleague: the detail shows the role, its permissions, and Add role, Remove role, and Delete', async () => {
    const detail = answered('GET', new RegExp(`^/api/v1/users/${colleague.id}$`))
    await usersTable().getByRole('link', { name: colleague.fullName, exact: true }).click()
    expect((await detail).status()).toBe(200)
    await expect(page.getByRole('heading', { name: colleague.fullName, exact: true })).toBeVisible()
    await expect(page.getByRole('definition').filter({ hasText: /^Auditor$/ })).toBeVisible()
    await expect(page.getByRole('list', { name: `Permissions of ${colleague.fullName}` }).getByRole('listitem')).toHaveText([
      'See the users',
      'See the agents',
      'Read the audit log',
      'Read the sensitive values of the audit log',
    ])
    const actions = page.getByRole('group', { name: `Actions for ${colleague.fullName}` })
    await expect(actions.getByRole('button', { name: 'Add role', exact: true })).toBeVisible()
    await expect(actions.getByRole('button', { name: 'Remove role', exact: true })).toBeVisible()
    await expect(actions.getByRole('button', { name: 'Delete', exact: true })).toBeVisible()
  })
  await capture('detail')

  await step('Add role: the dialog offers only the roles that the user does not have', async () => {
    await page.getByRole('button', { name: 'Add role', exact: true }).click()
    const dialog = page.getByRole('dialog', { name: `Add a role to ${colleague.fullName}` })
    await expect(dialog).toBeVisible()
    await dialog.getByRole('button', { name: /Role$/ }).click()
    await settle()
    await expect(page.getByRole('option')).toHaveText(['Admin', 'User manager'])
  })
  await capture('add-role-dialog', { fullPage: false })

  await step('pick User manager and confirm: the roles add up and the page shows both', async () => {
    const dialog = page.getByRole('dialog', { name: `Add a role to ${colleague.fullName}` })
    await page.getByRole('option', { name: 'User manager', exact: true }).click()
    const saved = answered('POST', new RegExp(`^/api/v1/users/${colleague.id}/roles$`))
    await dialog.getByRole('button', { name: 'Add role', exact: true }).click()
    const res = await saved
    expect(res.status()).toBe(200)
    const body = await res.json()
    expect(body.roles).toEqual(['Auditor', 'UserManager'])
    expect(body.permissions).toHaveLength(6)
    await expect(page.getByRole('definition').filter({ hasText: /^User manager, Auditor$|^Auditor, User manager$/ })).toBeVisible()
    await expect(page.getByRole('heading', { name: colleague.fullName, exact: true })).toBeFocused()
  })
  await capture('roles-added')

  await step('Remove role Auditor: read_users stays, because User manager gives it', async () => {
    await page.getByRole('button', { name: 'Remove role', exact: true }).click()
    const dialog = page.getByRole('dialog', { name: `Remove a role from ${colleague.fullName}` })
    await dialog.getByRole('button', { name: /Role$/ }).click()
    await settle()
    await page.getByRole('option', { name: 'Auditor', exact: true }).click()
    const saved = answered('DELETE', new RegExp(`^/api/v1/users/${colleague.id}/roles/Auditor$`))
    await dialog.getByRole('button', { name: 'Remove role', exact: true }).click()
    const res = await saved
    expect(res.status()).toBe(200)
    const body = await res.json()
    expect(body.roles).toEqual(['UserManager'])
    expect([...body.permissions].sort()).toEqual(['manage_user_access', 'manage_users', 'read_users'])
    await expect(page.getByRole('list', { name: `Permissions of ${colleague.fullName}` }).getByRole('listitem')).toHaveText([
      'See the users',
      'Manage users',
      'Manage the access of users',
    ])
  })
  await capture('role-removed')

  await step('Delete: the dialog asks for a confirmation', async () => {
    await page.getByRole('button', { name: 'Delete', exact: true }).click()
    await expect(page.getByRole('dialog', { name: `Delete ${colleague.fullName}?` })).toBeVisible()
    await settle()
  })
  await capture('delete-dialog', { fullPage: false })

  await step('Delete user: the Server deletes the user, the toast shows, and the list has no row for it', async () => {
    const deleted = answered('DELETE', new RegExp(`^/api/v1/users/${colleague.id}$`))
    await page.getByRole('dialog').getByRole('button', { name: 'Delete user', exact: true }).click()
    expect((await deleted).status()).toBe(204)
    await expect(page.getByText('User deleted', { exact: true })).toHaveCount(1)
    await expect(page.getByRole('heading', { name: 'Users', exact: true })).toBeFocused()
    await expect(usersTable().getByRole('link', { name: me.fullName, exact: true })).toBeVisible()
    await expect(usersTable().getByRole('link', { name: colleague.fullName, exact: true })).toHaveCount(0)
  })
  await capture('after-delete')
}
