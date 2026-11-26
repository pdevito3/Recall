export default async ({ page, step, capture, settle, expect, login, stack }) => {
  const desktop = { width: 1280, height: 800 }
  const phone = { width: 390, height: 844 }
  const answered = (method, pattern) =>
    page.waitForResponse((r) => r.request().method() === method && pattern.test(new URL(r.url()).pathname))
  const asPhone = async (name, options) => {
    await page.setViewportSize(phone)
    await capture(name, options)
    await page.setViewportSize(desktop)
  }

  await login()

  const token = await step('Personal access tokens: make a token that changes the journal, for the agent', async () => {
    await page.getByRole('navigation', { name: 'Main' }).getByRole('link', { name: 'Personal access tokens', exact: true }).click()
    await page.getByRole('button', { name: 'New token', exact: true }).click()
    const dialog = page.getByRole('dialog')
    await dialog.getByRole('textbox', { name: 'Name' }).fill('Import agent')
    await dialog.getByRole('checkbox', { name: 'Change my journal' }).click({ force: true })
    const created = answered('POST', /^\/api\/v1\/personal-access-tokens$/)
    await dialog.getByRole('button', { name: 'Create token', exact: true }).click()
    const body = await (await created).json()
    await dialog.getByRole('button', { name: 'Done', exact: true }).click()
    return body.value
  })

  const agent = async (method, path, body) => {
    const res = await fetch(new URL(path, stack.urls.web), {
      method,
      headers: { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json' },
      body: body && JSON.stringify(body),
    })
    return { status: res.status, body: await res.json().catch(() => null) }
  }

  await step('Imports in the sidebar: the page says how a batch gets made', async () => {
    const listed = answered('GET', /^\/api\/v1\/import-batches$/)
    await page.getByRole('navigation', { name: 'Main' }).getByRole('link', { name: 'Imports', exact: true }).click()
    expect((await listed).status()).toBe(200)
    await expect(page.getByRole('heading', { level: 1, name: 'Imports' })).toBeVisible()
    await expect(page.getByText(/^You have no import batches\. An agent opens a batch with a personal access token, then you review it here\.$/)).toBeVisible()
  })
  await capture('empty')
  await asPhone('empty-phone')

  const id = () => crypto.randomUUID()
  const e = { walk: id(), coffee: id(), london: id(), rain: id(), dinner: id(), garden: id(), evening: id(), gone: id(), late: id() }
  const coffee = (raw) => ({ clientSessionId: e.coffee, date: '2024-03-01', position: 0, time: '08:30', raw })
  const batch = await step('the agent opens a batch in New York time and puts entries on four days over REST', async () => {
    const opened = await agent('POST', '/api/v1/import-batches', { name: 'Diary 2024', timeZone: 'America/New_York' })
    expect(opened.status).toBe(201)
    const put = await agent('PUT', `/api/v1/import-batches/${opened.body.id}/entries`, {
      entries: [
        coffee('Woke early. **Coffee** on the porch.'),
        { clientSessionId: e.walk, date: '2024-03-01', position: 1, raw: 'Long walk by the river.\n\nSaw two herons.' },
        { clientSessionId: e.london, date: '2024-03-02', position: 0, time: '21:00', timeZone: 'Europe/London', raw: 'Flew to London.' },
        { clientSessionId: e.rain, date: '2024-03-03', position: 0, raw: 'Rainy day, stayed in.' },
        { clientSessionId: e.dinner, date: '2024-03-03', position: 1, time: '18:15', raw: 'Dinner with Sam.' },
        { clientSessionId: e.garden, date: '2024-03-04', position: 0, raw: 'Planted tomatoes.', moods: ['hopeful'], topics: ['Garden', 'Spring'] },
        { clientSessionId: e.evening, date: '2024-03-04', position: 1, time: '19:30', raw: 'Read on the couch.' },
        { clientSessionId: e.gone, date: '2024-03-04', position: 2, time: '23:00', raw: 'A line that the agent takes back.' },
      ],
    })
    expect(put.status).toBe(200)
    expect(put.body.outcomes.map((o) => o.outcome)).toEqual(Array(8).fill('Added'))
    return opened.body
  })

  let name
  const row = () => page.getByRole('list', { name: 'Your import batches' }).getByRole('listitem').filter({ has: page.getByRole('link', { name, exact: true }) })
  await step('the list shows the batch with its count, its zone, and Open', async () => {
    const listed = answered('GET', /^\/api\/v1\/import-batches$/)
    await page.reload()
    const shown = (await (await listed).json()).find((b) => b.id === batch.id)
    name = shown.name
    expect(shown.entryCount).toBe(8)
    await expect(row()).toContainText('Open')
    await expect(row()).toContainText('8 entries in Eastern Time')
  })
  await capture('list')
  await asPhone('list-phone')

  const card = (text) => page.getByRole('listitem', { name: /^Entry at / }).filter({ hasText: text })
  const counts = page.getByLabel('Counts of this batch')
  await step('the batch: entries by day, each start time in the zone of the entry, Moods and Topics', async () => {
    const days = answered('GET', /^\/api\/v1\/import-batches\/[^/]+\/days$/)
    await row().getByRole('link', { name, exact: true }).click()
    expect((await days).status()).toBe(200)
    await expect(page.getByRole('heading', { level: 1, name, exact: true })).toBeVisible()
    await expect(counts).toHaveText('8 entries: 8 kept, 0 rejected')
    for (const day of ['Friday, March 1, 2024', 'Saturday, March 2, 2024', 'Sunday, March 3, 2024', 'Monday, March 4, 2024'])
      await expect(page.getByRole('heading', { level: 2, name: day })).toBeVisible()
    await expect(card('Coffee on the porch')).toContainText('8:30 AM')
    await expect(card('Flew to London')).toContainText('9:00 PMUnited Kingdom Time')
    await expect(card('Long walk')).toContainText('12:01 PMTime from its place in the day')
    await expect(card('Planted tomatoes').getByRole('list', { name: 'Moods and topics' })).toHaveText(/Hopeful\s*Garden\s*Spring/i)
  })
  await capture('detail')
  await asPhone('detail-phone')

  await step('Edit: new words and a new time; the review sends both, and the entry shows them', async () => {
    await card('Coffee on the porch').getByRole('button', { name: 'Edit the entry at 8:30 AM' }).click()
    const form = page.getByRole('form', { name: 'Edit the entry' })
    const words = form.getByRole('textbox', { name: 'Words' })
    await words.click()
    await page.keyboard.press('ControlOrMeta+a')
    await page.keyboard.type('Woke early. Coffee on the porch, then a run.')
    await form.getByRole('spinbutton', { name: /^hour,/ }).click()
    await page.keyboard.type('0745')
    await expect(form.getByRole('group', { name: 'Time' })).toContainText(/7:45\W*AM/)
    await settle()
  })
  await capture('editing', { fullPage: false })

  await step('Save sends the whole review, and the entry shows the edit', async () => {
    const reviewed = answered('PUT', /\/entries\/review$/)
    await page.getByRole('form', { name: 'Edit the entry' }).getByRole('button', { name: 'Save', exact: true }).click()
    const res = await reviewed
    expect(res.status()).toBe(200)
    const sent = res.request().postDataJSON()
    expect(sent).toMatchObject({ clientSessionId: e.coffee, rejected: false, time: '07:45' })
    expect(sent.words).toContain('then a run.')
    await expect(card('then a run')).toContainText('7:45 AMEdited')
    await expect(page.getByRole('button', { name: 'Edit the entry at 7:45 AM' })).toBeFocused()
  })

  await step('Edit with no words: the form shows the refusal of the Server and keeps the change', async () => {
    await page.getByRole('button', { name: 'Edit the entry at 7:45 AM' }).click()
    const form = page.getByRole('form', { name: 'Edit the entry' })
    await form.getByRole('textbox', { name: 'Words' }).click()
    await page.keyboard.press('ControlOrMeta+a')
    await page.keyboard.press('Backspace')
    const reviewed = answered('PUT', /\/entries\/review$/)
    await form.getByRole('button', { name: 'Save', exact: true }).click()
    expect((await reviewed).status()).toBe(422)
    await expect(form.getByRole('alert')).toHaveText('Give the entry some words, or reject it to leave it out.')
    await settle()
  })
  await capture('blank-words-refused', { fullPage: false })

  await step('Cancel; the agent puts the entry again with other words; the entry says that the accept keeps the edit', async () => {
    await page.getByRole('form', { name: 'Edit the entry' }).getByRole('button', { name: 'Cancel', exact: true }).click()
    const put = await agent('PUT', `/api/v1/import-batches/${batch.id}/entries`, { entries: [coffee('Woke early. Coffee on the porch with the cat.')] })
    expect(put.body.outcomes[0].outcome).toBe('Changed')
    const entries = answered('GET', /\/entries$/)
    await page.reload()
    const read = (await (await entries).json()).entries.find((x) => x.clientSessionId === e.coffee)
    expect(read.wordsChangedAfterEdit).toBe(true)
    await expect(card('then a run')).toContainText('The import changed the words after your edit. The accept keeps your edit.')
  })
  await capture('edited-flag')
  await asPhone('edited-flag-phone')

  await step('Reject an entry: it shows Rejected, and the counts change', async () => {
    const reviewed = answered('PUT', /\/entries\/review$/)
    await card('Long walk').getByRole('button', { name: 'Reject the entry at 12:01 PM' }).click()
    expect((await reviewed).request().postDataJSON()).toEqual({ clientSessionId: e.walk, rejected: true, words: null, time: null })
    await expect(card('Long walk')).toContainText('Rejected')
    await expect(card('Long walk').getByRole('button', { name: 'Keep the entry at 12:01 PM' })).toBeVisible()
    await expect(counts).toHaveText('8 entries: 7 kept, 1 rejected')
  })
  await capture('rejected-entry')

  await step('Reject day: each entry of the day shows Rejected with its day, and the counts change', async () => {
    const reviewed = answered('PUT', /\/days\/review$/)
    await page.getByRole('button', { name: 'Reject Sunday, March 3, 2024' }).click()
    expect((await reviewed).request().postDataJSON()).toEqual({ date: '2024-03-03', rejected: true })
    await expect(card('Rainy day')).toContainText('Rejected with its day')
    await expect(card('Dinner with Sam')).toContainText('Rejected with its day')
    await expect(page.getByRole('button', { name: 'Keep Sunday, March 3, 2024' })).toBeVisible()
    await expect(counts).toHaveText('8 entries: 5 kept, 3 rejected')
  })
  await capture('rejected-day')
  await asPhone('rejected-day-phone')

  await step('the agent removes an entry that the page shows; its Reject shows the 404 of the Server, the page reads the batch again, and the focus goes to the heading', async () => {
    const removed = await agent('POST', `/api/v1/import-batches/${batch.id}/entries/remove`, { clientSessionIds: [e.gone] })
    expect(removed.body.outcomes[0].outcome).toBe('Removed')
    const reviewed = answered('PUT', /\/entries\/review$/)
    await card('agent takes back').getByRole('button', { name: 'Reject the entry at 11:00 PM' }).click()
    expect((await reviewed).status()).toBe(404)
    await expect(page.getByRole('alertdialog').filter({ hasText: 'This entry is not in the batch.' })).toBeVisible()
    await expect(card('agent takes back')).toHaveCount(0)
    await expect(counts).toHaveText('7 entries: 4 kept, 3 rejected')
    await expect(page.getByRole('heading', { level: 1, name, exact: true })).toBeFocused()
  })
  await capture('entry-gone', { fullPage: false })

  const dialog = page.getByRole('alertdialog', { name: 'Accept this batch?' })
  await step('Accept asks first, with the kept and the rejected counts', async () => {
    await page.getByRole('button', { name: 'Accept', exact: true }).click()
    await expect(dialog).toContainText('4 entries go into your journal, and 3 entries that you rejected stay out.')
    await settle()
  })
  await capture('accept-confirm', { fullPage: false })
  await page.setViewportSize(phone)
  await settle()
  await capture('accept-confirm-phone', { fullPage: false })
  await page.setViewportSize(desktop)

  await step('the agent puts one more entry while the dialog is open: the accept gets the 409, and the dialog shows the new counts', async () => {
    await agent('PUT', `/api/v1/import-batches/${batch.id}/entries`, {
      entries: [{ clientSessionId: e.late, date: '2024-03-05', position: 0, time: '10:00', raw: 'A late entry.' }],
    })
    const accepted = answered('POST', /\/accept$/)
    await dialog.getByRole('button', { name: 'Accept batch', exact: true }).click()
    expect((await accepted).status()).toBe(409)
    await expect(dialog.getByRole('alert')).toHaveText('This batch changed after you read it. Read it again, then accept it.')
    await expect(dialog).toContainText('5 entries go into your journal, and 3 entries that you rejected stay out.')
    await settle()
  })
  await capture('accept-stale', { fullPage: false })

  const result = await step('Accept batch: the page shows the counts of the result, with a link to the journal', async () => {
    const accepted = answered('POST', /\/accept$/)
    await dialog.getByRole('button', { name: 'Accept batch', exact: true }).click()
    const res = await accepted
    expect(res.status()).toBe(200)
    const body = await res.json()
    expect(body.result).toEqual({ sessionsMade: 5, entriesRejected: 3, daysRejected: 1, skippedInJournal: 0, skippedDeleted: 0 })
    const list = page.getByRole('list', { name: 'Result of the accept' })
    await expect(list).toContainText('5 entries made in your journal')
    await expect(list).toContainText('3 entries rejected')
    await expect(list).toContainText('1 day rejected')
    return body
  })
  await capture('accept-result')
  await asPhone('accept-result-phone')

  await step('Go to your journal: the Sessions of the accept have the start times of the review', async () => {
    const timeline = answered('GET', /^\/api\/v1\/sessions$/)
    await page.getByRole('link', { name: 'Go to your journal' }).click()
    const rows = (await (await timeline).json()).items.filter((s) => s.importBatchId === result.id)
    const startOf = (instant) => new Date(instant).toISOString()
    expect(rows.map((s) => startOf(s.startedAt)).sort()).toEqual([
      '2024-03-01T12:45:00.000Z',
      '2024-03-02T21:00:00.000Z',
      '2024-03-04T17:00:00.000Z',
      '2024-03-05T00:30:00.000Z',
      '2024-03-05T15:00:00.000Z',
    ])
    await expect(page.getByText('Woke early. Coffee on the porch, then a run.')).toBeVisible()
  })
  await capture('journal')

  await step('Settings: the import review says who can accept, and a change sends it', async () => {
    await page.getByRole('navigation', { name: 'Main' }).getByRole('link', { name: 'Settings', exact: true }).click()
    const box = page.getByRole('checkbox', { name: 'Only I can accept an import batch' })
    await expect(box).toBeChecked()
    await expect(page.getByText(/^With this on, only you can accept an import batch, in Recall in a browser\./)).toBeVisible()
    const changed = answered('PUT', /^\/api\/v1\/me\/settings\/import-review$/)
    await box.click({ force: true })
    const res = await changed
    expect(res.status()).toBe(200)
    expect(res.request().postDataJSON()).toMatchObject({ requireImportReview: false })
    await expect(box).not.toBeChecked()
    const back = answered('PUT', /^\/api\/v1\/me\/settings\/import-review$/)
    await box.click({ force: true })
    expect((await back).status()).toBe(200)
    await expect(box).toBeChecked()
  })
  await capture('setting')
  await asPhone('setting-phone')

  const second = await step('Discard asks first with the name and the count, then the batch leaves the list', async () => {
    const opened = await agent('POST', '/api/v1/import-batches', { name: 'Notes to throw away', timeZone: 'Europe/Berlin' })
    await agent('PUT', `/api/v1/import-batches/${opened.body.id}/entries`, {
      entries: [{ clientSessionId: id(), date: '2023-07-01', position: 0, raw: 'Draft.' }],
    })
    const listed = answered('GET', /^\/api\/v1\/import-batches$/)
    await page.getByRole('navigation', { name: 'Main' }).getByRole('link', { name: 'Imports', exact: true }).click()
    const shown = (await (await listed).json()).find((b) => b.id === opened.body.id)
    await page.getByRole('button', { name: `Discard ${shown.name}` }).click()
    await expect(page.getByRole('alertdialog')).toContainText('This batch and its 1 entry leave Recall, and nothing of it reaches your journal.')
    await settle()
    return shown
  })
  await capture('discard-confirm', { fullPage: false })

  await step('Discard batch: the batch leaves the list, and the accepted batch stays with no Discard', async () => {
    const discarded = answered('DELETE', /^\/api\/v1\/import-batches\/[^/]+$/)
    await page.getByRole('alertdialog').getByRole('button', { name: 'Discard batch', exact: true }).click()
    expect((await discarded).status()).toBe(204)
    await expect(page.getByRole('link', { name: second.name })).toHaveCount(0)
    await expect(row()).toContainText('In your journal')
    await expect(row().getByRole('button')).toHaveCount(0)
  })
  await capture('list-after-discard')

  await step('a batch that does not exist says so, with a link to the imports', async () => {
    await page.goto(`/imports/${second.id}`)
    await expect(page.getByText('This import batch does not exist. It may have been discarded.')).toBeVisible()
  })
  await capture('missing')
}
