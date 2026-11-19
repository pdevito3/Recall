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
    await expect(page.getByText('You have no misheard words yet. Add a word that dictation gets wrong.', { exact: true })).toBeVisible()
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
    await page.getByRole('searchbox', { name: 'Search misheard words' }).fill('CUBE')
    await expect.poll(() => new URL(page.url()).searchParams.get('search')).toBe('CUBE')
    await expect(list.getByText(added.canonicalTerm, { exact: true })).toBeVisible()
    await page.reload()
    await expect(list.getByText(added.canonicalTerm, { exact: true })).toBeVisible()
  })
  await capture('searched')

  await step('a search with no match says so', async () => {
    await page.getByRole('searchbox', { name: 'Search misheard words' }).fill('no such word')
    await expect(page.getByText('No misheard word has “no such word” in its term or its mishearings.', { exact: true })).toBeVisible()
    await page.getByRole('searchbox', { name: 'Search misheard words' }).fill('')
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
    await expect(page.getByText('You have no misheard words yet. Add a word that dictation gets wrong.', { exact: true })).toBeVisible()
    await expect(page.getByRole('heading', { level: 1, name: 'Corrections' })).toBeFocused()
  })
  await capture('deleted')

  const words = page.getByRole('list', { name: 'Your known words' })
  const wordInput = page.getByRole('textbox', { name: 'Known words' })

  const known = await step('type three known words, each with Enter: each is a pill, and the list of corrections stays empty', async () => {
    const rows = []
    for (const word of ['Radancy', 'Profisee', 'Aspire']) {
      await wordInput.fill(word)
      const answer = page.waitForResponse(isCall('POST', '/api/v1/corrections'))
      await wordInput.press('Enter')
      const response = await answer
      expect(response.status()).toBe(201)
      expect(response.request().postDataJSON()).toEqual({ canonicalTerm: word, mishearings: [], hardReplace: false })
      const row = await response.json()
      await expect(words.getByRole('listitem').filter({ hasText: row.canonicalTerm })).toBeVisible()
      await expect(wordInput).toHaveValue('')
      await expect(wordInput).toBeFocused()
      rows.push(row)
    }
    await expect(page.getByText('You have no misheard words yet. Add a word that dictation gets wrong.', { exact: true })).toBeVisible()
    return rows
  })
  await capture('known-words')

  await step('a known word in another case is refused inline, with no request', async () => {
    let posted = false
    const watch = (request) => {
      if (request.method() === 'POST' && new URL(request.url()).pathname === '/api/v1/corrections') posted = true
    }
    page.on('request', watch)
    await wordInput.fill(known[0].canonicalTerm.toLowerCase())
    await wordInput.press('Enter')
    await expect(page.getByRole('alert').filter({ hasText: `“${known[0].canonicalTerm}” is already a known word.` })).toBeVisible()
    await expect(wordInput).toHaveValue(known[0].canonicalTerm.toLowerCase())
    page.off('request', watch)
    expect(posted).toBe(false)
  })
  await capture('known-word-in-use')

  await step('the × of a pill removes that known word, and the focus goes back to the input', async () => {
    await wordInput.fill('')
    const answer = page.waitForResponse(isCall('DELETE', `/api/v1/corrections/${known[1].id}`))
    await words.getByRole('button', { name: `Remove ${known[1].canonicalTerm}`, exact: true }).click()
    const response = await answer
    expect(response.status()).toBe(204)
    expect(new URL(response.url()).searchParams.get('ifVersion')).toBe(String(known[1].version))
    await expect(words.getByRole('listitem').filter({ hasText: known[1].canonicalTerm })).toHaveCount(0)
    await expect(words.getByRole('listitem')).toHaveCount(2)
    await expect(wordInput).toBeFocused()
  })
  await capture('known-word-removed')

  await step('a new correction whose one mishearing is the term in another case is refused, with no request', async () => {
    await page.getByRole('button', { name: 'Add correction', exact: true }).click()
    await dialog.getByRole('textbox', { name: 'Term' }).fill('GRDB')
    await dialog.getByRole('textbox', { name: 'Mishearings' }).fill('grdb')
    let posted = false
    const watch = (request) => {
      if (request.method() === 'POST' && new URL(request.url()).pathname === '/api/v1/corrections') posted = true
    }
    page.on('request', watch)
    await dialog.getByRole('button', { name: 'Save correction', exact: true }).click()
    await expect(
      dialog.getByText('A mishearing must differ from the term. To keep a word with no mishearings, add it under Known words.', { exact: true }),
    ).toBeVisible()
    page.off('request', watch)
    expect(posted).toBe(false)
  })
  await capture('mishearing-is-the-term', { fullPage: false })

  const misheard = await step('fix the mishearing and save: the row is a misheard word', async () => {
    await dialog.getByRole('textbox', { name: 'Mishearings' }).fill('grid b')
    const answer = page.waitForResponse(isCall('POST', '/api/v1/corrections'))
    await dialog.getByRole('button', { name: 'Save correction', exact: true }).click()
    const response = await answer
    expect(response.status()).toBe(201)
    await expect(dialog).toHaveCount(0)
    const row = await response.json()
    await expect(list.getByText(row.canonicalTerm, { exact: true })).toBeVisible()
    return row
  })

  await step('Edit it and clear the mishearings', async () => {
    await page.getByRole('button', { name: `Edit ${misheard.canonicalTerm}`, exact: true }).click()
    await expect(dialog.getByText('Clear them to keep the term as a known word.', { exact: false })).toBeVisible()
    await dialog.getByRole('textbox', { name: 'Mishearings' }).fill('')
  })
  await capture('edit-clear-mishearings', { fullPage: false })

  await step('save: the term is a known word, the toast says so, and the heading has the focus', async () => {
    const answer = page.waitForResponse(isCall('PUT', `/api/v1/corrections/${misheard.id}`))
    await dialog.getByRole('button', { name: 'Save correction', exact: true }).click()
    const response = await answer
    expect(response.status()).toBe(200)
    expect(response.request().postDataJSON()).toEqual({ canonicalTerm: misheard.canonicalTerm, mishearings: [], hardReplace: false, version: misheard.version })
    const row = await response.json()
    expect(row.mishearings).toEqual([])
    await expect(dialog).toHaveCount(0)
    await expect(page.getByRole('alertdialog').filter({ hasText: `“${row.canonicalTerm}” is now a known word.` })).toBeVisible()
    await expect(words.getByRole('listitem').filter({ hasText: row.canonicalTerm })).toBeVisible()
    await expect(page.getByRole('button', { name: `Edit ${row.canonicalTerm}`, exact: true })).toHaveCount(0)
    await expect(page.getByText('You have no misheard words yet. Add a word that dictation gets wrong.', { exact: true })).toBeVisible()
    await expect(page.getByRole('heading', { level: 1, name: 'Corrections' })).toBeFocused()
  })
  await capture('edited-to-known-word', { fullPage: false })

  await step('on a phone, the pills keep a gap between each × and the next pill', async () => {
    await page.setViewportSize({ width: 390, height: 844 })
    await page.reload()
    await expect(words.getByRole('listitem')).toHaveCount(3)
  })
  await capture('known-words-phone', { fullPage: false })

  await step('on a phone in the dark theme, the pills keep their contrast', async () => {
    await page.evaluate(() => localStorage.setItem('ui-theme', 'dark'))
    await page.reload()
    await expect(page.locator('html')).toHaveClass(/\bdark\b/)
    await expect(words.getByRole('listitem')).toHaveCount(3)
  })
  await capture('known-words-phone-dark', { fullPage: false })
}
