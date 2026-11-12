const isCall = (method, path) => (r) => r.request().method() === method && new URL(r.url()).pathname.startsWith(path)

export default async ({ page, step, capture, expect, login }) => {
  const list = page.getByRole('list', { name: 'Your corrections' })
  const dialog = page.getByRole('dialog')
  const term = `Kubernetes${Date.now().toString(36)}`.padEnd(60, 'x')

  await step('log in with no corrections', async () => {
    await login()
  })

  await step('Corrections in the sidebar: the empty list says how to start', async () => {
    await page.goto('/')
    await page.getByRole('link', { name: 'Corrections', exact: true }).click()
    await expect(page.getByRole('heading', { level: 1, name: 'Corrections' })).toBeVisible()
    await expect(page.getByText('You have no corrections yet. Add a word that dictation gets wrong.', { exact: true })).toBeVisible()
  })
  await capture('empty')

  const added = await step('add a correction with two mishearings that always replaces', async () => {
    await page.getByRole('button', { name: 'Add correction', exact: true }).click()
    await dialog.getByRole('textbox', { name: 'Term' }).fill(term)
    await dialog.getByRole('textbox', { name: 'Mishearings' }).fill('cooper netties\n\nCube rnetes')
    await dialog.getByText('Always replace', { exact: true }).click()
    const answer = page.waitForResponse(isCall('POST', '/api/v1/corrections'))
    await dialog.getByRole('button', { name: 'Save correction', exact: true }).click()
    const response = await answer
    expect(response.status()).toBe(201)
    const row = await response.json()
    expect(row.mishearings).toEqual(['cooper netties', 'Cube rnetes'])
    expect(row.hardReplace).toBe(true)
    await expect(dialog).toHaveCount(0)
    await expect(list.getByText(row.canonicalTerm, { exact: true })).toBeVisible()
    await expect(list.getByText(`Heard as ${row.mishearings.join(', ')}`, { exact: true })).toBeVisible()
    await expect(list.getByText('Always replaced', { exact: true })).toBeVisible()
    return row
  })
  await capture('added')

  await step('a search for a mishearing in another case finds it, and the URL keeps it', async () => {
    await page.getByRole('searchbox', { name: 'Search corrections' }).fill('CUBE')
    await expect.poll(() => new URL(page.url()).searchParams.get('search')).toBe('CUBE')
    await expect(list.getByText(added.canonicalTerm, { exact: true })).toBeVisible()
    await page.reload()
    await expect(list.getByText(added.canonicalTerm, { exact: true })).toBeVisible()
  })
  await capture('searched')

  await step('a search with no match says so', async () => {
    await page.getByRole('searchbox', { name: 'Search corrections' }).fill('no such word')
    await expect(page.getByText('No correction has “no such word” in its term or its mishearings.', { exact: true })).toBeVisible()
    await page.getByRole('searchbox', { name: 'Search corrections' }).fill('')
    await expect(list.getByText(added.canonicalTerm, { exact: true })).toBeVisible()
  })

  await step('Edit opens the saved values', async () => {
    await page.getByRole('button', { name: `Edit ${added.canonicalTerm}`, exact: true }).click()
    await expect(dialog.getByRole('textbox', { name: 'Mishearings' })).toHaveValue(added.mishearings.join('\n'))
    await expect(dialog.getByRole('checkbox', { name: 'Always replace' })).toBeChecked()
  })
  await capture('edit-dialog', { fullPage: false })

  const edited = await step('add a mishearing and clear Always replace', async () => {
    await dialog.getByRole('textbox', { name: 'Mishearings' }).fill(`${added.mishearings.join('\n')}\ncooper nettys`)
    await dialog.getByText('Always replace', { exact: true }).click()
    const answer = page.waitForResponse(isCall('PUT', `/api/v1/corrections/${added.id}`))
    await dialog.getByRole('button', { name: 'Save correction', exact: true }).click()
    const response = await answer
    expect(response.status()).toBe(200)
    expect(response.request().postDataJSON().version).toBe(added.version)
    const row = await response.json()
    expect(row.hardReplace).toBe(false)
    await expect(dialog).toHaveCount(0)
    await expect(list.getByText(`Heard as ${row.mishearings.join(', ')}`, { exact: true })).toBeVisible()
    await expect(list.getByText('Always replaced', { exact: true })).toHaveCount(0)
    return row
  })
  await capture('edited')

  await step('delete it after a confirm: the list is empty and the heading has the focus', async () => {
    await page.getByRole('button', { name: `Delete ${edited.canonicalTerm}`, exact: true }).click()
    await expect(dialog.getByRole('heading', { name: `Delete ${edited.canonicalTerm}?` })).toBeVisible()
    const answer = page.waitForResponse(isCall('DELETE', `/api/v1/corrections/${added.id}`))
    await dialog.getByRole('button', { name: 'Delete correction', exact: true }).click()
    expect((await answer).status()).toBe(204)
    await expect(page.getByText('You have no corrections yet. Add a word that dictation gets wrong.', { exact: true })).toBeVisible()
    await expect(page.getByRole('heading', { level: 1, name: 'Corrections' })).toBeFocused()
  })
  await capture('deleted')
}
