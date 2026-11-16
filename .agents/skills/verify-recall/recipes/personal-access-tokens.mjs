export default async ({ page, step, capture, settle, expect, login, stack }) => {
  const name = `Laptop${'x'.repeat(60)}${Date.now().toString(36)}`
  const answered = (method, pattern) =>
    page.waitForResponse((r) => r.request().method() === method && pattern.test(new URL(r.url()).pathname))
  const mine = () => page.getByRole('list', { name: 'Your personal access tokens' })
  const statusWithToken = async (value) =>
    (await fetch(new URL('/api/v1/sessions', stack.urls.web), { headers: { Authorization: `Bearer ${value}` } })).status

  const me = await step('log in as the Owner', async () => {
    await login()
    return page.evaluate(async () => {
      const headers = { 'X-CSRF': '1' }
      const current = await (await fetch('/api/v1/users/me', { headers })).json()
      return (await fetch(`/api/v1/users/by-identifier/${encodeURIComponent(current.identifier)}`, { headers })).json()
    })
  })

  await step('Personal access tokens in the sidebar: the page says that the person has no token', async () => {
    const listed = answered('GET', /^\/api\/v1\/personal-access-tokens$/)
    await page.getByRole('navigation', { name: 'Main' }).getByRole('link', { name: 'Personal access tokens', exact: true }).click()
    expect((await listed).status()).toBe(200)
    await expect(page.getByRole('heading', { level: 1, name: 'Personal access tokens' })).toBeVisible()
    await expect(page.getByText('You have no personal access tokens yet. Make one for each client that you use.', { exact: true })).toBeVisible()
  })
  await capture('empty')

  await step('New token: the form offers the permissions of the person in words, and 90 days by default', async () => {
    await page.getByRole('button', { name: 'New token', exact: true }).click()
    const dialog = page.getByRole('dialog', { name: 'New personal access token' })
    await expect(dialog).toBeVisible()
    await expect(dialog.getByRole('checkbox', { name: 'See the users', exact: true })).toBeVisible()
    await expect(dialog).not.toContainText('manage_users')
    await expect(dialog.getByRole('button', { name: /Expires$/ })).toContainText(/^In 90 days, on /)
    await settle()
  })
  await capture('form', { fullPage: false })

  const created = await step('Create token: the dialog shows the value of the answer once, with Copy', async () => {
    const dialog = page.getByRole('dialog')
    await dialog.getByRole('textbox', { name: 'Name' }).fill(name)
    await dialog.getByRole('checkbox', { name: 'Change my journal' }).click({ force: true })
    await expect(dialog.getByRole('checkbox', { name: 'Read my journal' })).toBeChecked()
    const answer = answered('POST', /^\/api\/v1\/personal-access-tokens$/)
    await dialog.getByRole('button', { name: 'Create token', exact: true }).click()
    const res = await answer
    expect(res.status()).toBe(201)
    const body = await res.json()
    expect(body.value).toMatch(/^rcl_pat_/)
    expect(body.token).toMatchObject({ readJournal: true, writeJournal: true, permissions: [] })
    await expect(dialog.getByText(body.value, { exact: true })).toBeVisible()
    await dialog.getByRole('button', { name: 'Copy token', exact: true }).click()
    await expect(page.getByRole('alertdialog').filter({ hasText: 'Token copied' })).toBeVisible()
    await settle()
    return body
  })
  await capture('created', { fullPage: false })

  await step('the value works as a bearer from outside the browser', async () => {
    expect(await statusWithToken(created.value)).toBe(200)
  })

  await step('Done, then reload: the list shows the token of the answer, and the page holds no value', async () => {
    await page.getByRole('dialog').getByRole('button', { name: 'Done', exact: true }).click()
    await expect(page.getByRole('dialog')).toHaveCount(0)
    const listed = answered('GET', /^\/api\/v1\/personal-access-tokens$/)
    await page.reload()
    const rows = await (await listed).json()
    const row = rows.find((r) => r.id === created.token.id)
    expect(row).toBeTruthy()
    expect(JSON.stringify(rows)).not.toContain(created.value)
    const shown = mine().getByRole('listitem', { name: row.name, exact: true })
    await expect(shown).toContainText('Active')
    await expect(shown).toContainText('Reads and changes the journal')
    await expect(page.getByText(/rcl_pat_/)).toHaveCount(0)
  })
  await capture('list-after-reload')

  const section = () => page.getByRole('list', { name: `Personal access tokens of ${me.fullName}` })

  await step('the page of the user: the Admin section lists the token with Revoke and no value', async () => {
    const listed = answered('GET', new RegExp(`^/api/v1/users/${me.id}/personal-access-tokens$`))
    await page.goto(`/users/${me.id}`)
    const rows = await (await listed).json()
    const row = rows.find((r) => r.id === created.token.id)
    await expect(section().getByRole('listitem', { name: row.name, exact: true })).toBeVisible()
    await expect(page.getByRole('button', { name: `Revoke ${row.name}`, exact: true })).toBeVisible()
    await expect(page.getByText(/rcl_pat_/)).toHaveCount(0)
  })
  await capture('admin-section')

  await step('Revoke there: the row shows Revoked, and the bearer gets 401', async () => {
    const tokenName = await section().getByRole('listitem').filter({ hasText: 'Active' }).getAttribute('aria-label')
    await page.getByRole('button', { name: `Revoke ${tokenName}`, exact: true }).click()
    await settle()
    const revoked = answered('POST', new RegExp(`^/api/v1/personal-access-tokens/${created.token.id}/revoke$`))
    await page.getByRole('dialog').getByRole('button', { name: 'Revoke token', exact: true }).click()
    expect((await revoked).status()).toBe(204)
    await expect(section().getByRole('listitem', { name: tokenName, exact: true })).toContainText('Revoked')
    await expect(page.getByRole('heading', { level: 1, name: me.fullName })).toBeFocused()
    expect(await statusWithToken(created.value)).toBe(401)
  })
  await capture('revoked')
}
