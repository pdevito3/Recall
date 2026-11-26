import { readFileSync } from 'node:fs'

const skillFile = readFileSync(new URL('../../../../frontend/src/domain/imports/recall-import.md', import.meta.url), 'utf8')

export default async ({ page, context, step, capture, settle, expect, login, stack }) => {
  const desktop = { width: 1280, height: 800 }
  const phone = { width: 390, height: 844 }
  const answered = (method, pattern) =>
    page.waitForResponse((r) => r.request().method() === method && pattern.test(new URL(r.url()).pathname))
  // A capture shows the page as a person sees it: no hover state of the last click, and no toast of an earlier step.
  const shot = async (name, { keepToast = false, ...options } = {}) => {
    if (!keepToast) {
      // Close the first toast each time: the list moves when a toast closes, so a stored second locator can point at
      // nothing. A toast can also close by itself between the count and the click, so a missed click only counts again.
      const closers = page.locator('[data-slot="toast-region"]').getByRole('button', { name: 'Close' })
      while ((await closers.count()) > 0) await closers.first().click({ timeout: 2000 }).catch(() => {})
      await expect(page.locator('[data-slot="toast-region"]').getByRole('button', { name: 'Close' })).toHaveCount(0)
    }
    const size = page.viewportSize()
    await page.mouse.move(size.width - 2, size.height - 2)
    await capture(name, options)
  }
  const asPhone = async (name, options) => {
    await page.setViewportSize(phone)
    await settle()
    await shot(name, options)
    await page.setViewportSize(desktop)
    await settle()
  }
  const asDark = async (name, options) => {
    await page.emulateMedia({ colorScheme: 'dark' })
    await settle()
    await shot(name, options)
    await page.emulateMedia({ colorScheme: 'light' })
    await settle()
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
    await expect(page.getByText(/^You have no import batches\. When an agent opens a batch, you review it here\.$/)).toBeVisible()
  })
  await shot('empty')
  await asPhone('empty-phone')

  const skillPanel = page.getByRole('region', { name: 'Import with an agent', exact: true })
  const skillBox = skillPanel.getByRole('region', { name: 'The text of the recall-import skill', exact: true, includeHidden: true })
  const skillToggle = skillPanel.getByRole('button', { name: 'The recall-import skill', exact: true })
  const copySkill = skillPanel.getByRole('button', { name: 'Copy skill', exact: true })
  const toast = (text) => page.locator('[data-slot="toast-region"]').getByText(text, { exact: true })
  const mcpUrl = `${new URL(stack.urls.web).origin}/mcp`
  const atSkill = async (name, options) => {
    await skillPanel.scrollIntoViewIfNeeded()
    await shot(name, { fullPage: false, ...options })
  }
  const panelShots = async (name) => {
    await atSkill(name)
    await page.setViewportSize(phone)
    await settle()
    await atSkill(`${name}-phone`)
    await page.setViewportSize(desktop)
    await settle()
    await page.emulateMedia({ colorScheme: 'dark' })
    await settle()
    await atSkill(`${name}-dark`)
    await page.emulateMedia({ colorScheme: 'light' })
    await settle()
  }
  await step('the panel after the list: the MCP URL of this Recall, and the whole recall-import file in a closed disclosure', async () => {
    await expect(skillPanel.getByText(/^Connect your agent, such as Claude Code or Claude Desktop, to this MCP URL with a personal access token that can change your journal\.$/)).toBeVisible()
    await expect(skillPanel.getByRole('link', { name: 'personal access token', exact: true })).toHaveAttribute('href', '/settings/personal-access-tokens')
    await expect(skillPanel.getByRole('group', { name: 'MCP URL', exact: true }).getByText(mcpUrl, { exact: true })).toBeVisible()
    await expect(skillPanel.getByText(/save it as a skill of the agent, or paste it at the start of a chat\.$/)).toBeVisible()
    await expect(skillToggle).toHaveAttribute('aria-expanded', 'false')
    await expect(skillBox).toBeHidden()
    expect(await skillBox.textContent()).toBe(skillFile)
  })
  await panelShots('panel-empty')
  await step('the MCP URL of the panel answers an MCP client with the token of the agent', async () => {
    const res = await fetch(mcpUrl, {
      method: 'POST',
      headers: { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json', Accept: 'application/json, text/event-stream' },
      body: JSON.stringify({ jsonrpc: '2.0', id: 1, method: 'tools/list' }),
    })
    expect(res.status).toBe(200)
    expect(await res.text()).toContain('"put_import_entries"')
  })
  await step('Copy MCP URL puts the URL on the clipboard and says so', async () => {
    await context.grantPermissions(['clipboard-read', 'clipboard-write'])
    await skillPanel.getByRole('button', { name: 'Copy MCP URL', exact: true }).click()
    await expect(toast('MCP URL copied')).toBeVisible()
    expect(await page.evaluate(() => navigator.clipboard.readText())).toBe(mcpUrl)
  })
  await atSkill('mcp-url-copied', { keepToast: true })
  await step('Copy skill, with the skill closed, puts the whole file on the clipboard, says so, and keeps the focus', async () => {
    await copySkill.click()
    await expect(toast('Skill copied')).toBeVisible()
    expect(await page.evaluate(() => navigator.clipboard.readText())).toBe(skillFile)
    await expect(copySkill).toBeFocused()
  })
  await atSkill('skill-copied', { keepToast: true })
  await step('the disclosure opens the skill, and a list item wraps under its words', async () => {
    await skillToggle.click()
    await expect(skillToggle).toHaveAttribute('aria-expanded', 'true')
    await expect(skillBox).toBeVisible()
    const item = skillBox.locator('span').filter({ hasText: /^- `list_journal_days`/ })
    expect(await item.evaluate((line) => getComputedStyle(line).textIndent)).not.toBe('0px')
    // Scroll the box to the list of tools, so that the shots show how a wrapped list item lines up.
    await skillBox.evaluate((box) => {
      const tools = [...box.children].find((line) => line.textContent.startsWith('## The tools'))
      box.scrollTop += tools.getBoundingClientRect().top - box.getBoundingClientRect().top - 12
    })
  })
  await panelShots('skill-open')
  await step('Tab from Copy skill puts the keyboard focus on the box of the skill', async () => {
    await copySkill.focus()
    await page.keyboard.press('Tab')
    await expect(skillBox).toBeFocused()
  })
  await atSkill('skill-focus')
  await step('a browser that refuses the copy: the skill opens with its whole text selected, and a toast says to copy it by hand', async () => {
    await skillToggle.click()
    await expect(skillBox).toBeHidden()
    await page.evaluate(() => {
      navigator.clipboard.writeText = () => Promise.reject(new Error('Not allowed'))
      document.execCommand = () => false
    })
    await copySkill.click()
    await expect(toast('The browser did not copy the skill. Recall selected the whole text of the skill, so copy it by hand.')).toBeVisible()
    await expect(skillBox).toBeVisible()
    expect(await page.evaluate(() => window.getSelection().toString())).toBe(skillFile.trimEnd())
  })
  await atSkill('skill-copy-failed', { keepToast: true })
  await page.reload()
  await expect(page.getByRole('heading', { level: 1, name: 'Imports' })).toBeVisible()

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
    await expect(row()).toContainText('8 entries. Times in Eastern Time.')
  })
  await shot('list')
  await asPhone('list-phone')
  await asDark('list-dark')
  await panelShots('panel-batches')

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
    await expect(card('Long walk')).toContainText('12:01 PMNo time given. Set from its place in the day.')
    await expect(card('Planted tomatoes').getByRole('list', { name: 'Moods and topics' })).toHaveText(/Hopeful\s*Garden\s*Spring/i)
  })
  await shot('detail')
  await asPhone('detail-phone')
  await asDark('detail-dark')

  await step('Edit: a few more words that keep the bold word, and a new time; the review sends both, and the entry shows them', async () => {
    await card('Coffee on the porch').getByRole('button', { name: 'Edit the entry at 8:30 AM' }).click()
    const form = page.getByRole('form', { name: 'Edit the entry' })
    const words = form.getByRole('textbox', { name: 'Words' })
    await words.click()
    await page.keyboard.press('ControlOrMeta+End')
    await page.keyboard.press('Backspace')
    await page.keyboard.type(', then a run.')
    await expect(words.locator('strong')).toHaveText('Coffee')
    await expect(form.getByText('In Eastern Time. Leave it empty to use the import time.')).toBeVisible()
    await form.getByRole('spinbutton', { name: /^hour,/ }).click()
    await page.keyboard.type('0745')
    await expect(form.getByRole('group', { name: 'Time' })).toContainText(/7:45\W*AM/)
    await settle()
  })
  await shot('editing', { fullPage: false })
  await asPhone('editing-phone', { fullPage: false })
  await asDark('editing-dark', { fullPage: false })

  await step('Save sends the whole review, and the entry shows the edit', async () => {
    const reviewed = answered('PUT', /\/entries\/review$/)
    await page.getByRole('form', { name: 'Edit the entry' }).getByRole('button', { name: 'Save', exact: true }).click()
    const res = await reviewed
    expect(res.status()).toBe(200)
    const sent = res.request().postDataJSON()
    expect(sent).toMatchObject({ clientSessionId: e.coffee, rejected: false, time: '07:45' })
    expect(sent.words).toContain('then a run.')
    expect(JSON.parse(sent.words).content[0].content.find((node) => node.text === 'Coffee').marks).toEqual([{ type: 'bold' }])
    await expect(card('then a run')).toContainText('7:45 AMEdited')
    await expect(card('then a run').locator('strong')).toHaveText('Coffee')
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
    await expect(form.getByRole('textbox', { name: 'Words' })).toBeFocused()
    await expect(form.getByRole('textbox', { name: 'Words' })).toHaveAttribute('aria-invalid', 'true')
    await expect(form.getByRole('button', { name: 'Use the import words', exact: true })).toHaveAccessibleDescription('Save or cancel your change first.')
    await expect(form.getByText('Save or cancel your change first.')).toBeVisible()
    await settle()
  })
  await shot('blank-words-refused', { fullPage: false })

  await step('Cancel; the agent puts the entry again with other words; the entry says that the accept keeps the edit', async () => {
    await page.getByRole('form', { name: 'Edit the entry' }).getByRole('button', { name: 'Cancel', exact: true }).click()
    const put = await agent('PUT', `/api/v1/import-batches/${batch.id}/entries`, { entries: [coffee('Woke early. Coffee on the porch with the cat.')] })
    expect(put.body.outcomes[0].outcome).toBe('Changed')
    const entries = answered('GET', /\/entries$/)
    await page.reload()
    const read = (await (await entries).json()).entries.find((x) => x.clientSessionId === e.coffee)
    expect(read.wordsChangedAfterEdit).toBe(true)
    await expect(card('then a run')).toContainText(
      'The import changed the words after your edit. The accept keeps your edit. To use the new words, choose “Edit”, then “Use the import words”.',
    )
  })
  await shot('edited-flag')
  await asPhone('edited-flag-phone')

  await step('Reject an entry: it shows Rejected, and the counts change', async () => {
    const reviewed = answered('PUT', /\/entries\/review$/)
    await card('Long walk').getByRole('button', { name: 'Reject the entry at 12:01 PM' }).click()
    expect((await reviewed).request().postDataJSON()).toEqual({ clientSessionId: e.walk, rejected: true, words: null, time: null })
    await expect(card('Long walk')).toContainText('Rejected')
    await expect(card('Long walk').getByRole('button', { name: 'Keep the entry at 12:01 PM' })).toBeVisible()
    await expect(counts).toHaveText('8 entries: 7 kept, 1 rejected')
  })
  await shot('rejected-entry')

  await step('Reject day: the heading says the day is rejected, each entry says rejected with the day, and the counts change', async () => {
    const reviewed = answered('PUT', /\/days\/review$/)
    await page.getByRole('button', { name: 'Reject Sunday, March 3, 2024' }).click()
    expect((await reviewed).request().postDataJSON()).toEqual({ date: '2024-03-03', rejected: true })
    await expect(card('Rainy day')).toContainText('Rejected with the day')
    await expect(card('Dinner with Sam')).toContainText('Rejected with the day')
    await expect(page.getByRole('region', { name: 'Sunday, March 3, 2024' })).toContainText('2 entries, day rejected')
    await expect(page.getByRole('button', { name: 'Keep Sunday, March 3, 2024' })).toBeVisible()
    await expect(counts).toHaveText('8 entries: 5 kept, 3 rejected')
  })
  await shot('rejected-day')
  await asPhone('rejected-day-phone')

  await step('the agent removes an entry that the page shows; its Reject shows the 404 of the Server, the page reads the batch again, and the focus goes to the heading', async () => {
    const removed = await agent('POST', `/api/v1/import-batches/${batch.id}/entries/remove`, { clientSessionIds: [e.gone] })
    expect(removed.body.outcomes[0].outcome).toBe('Removed')
    const reviewed = answered('PUT', /\/entries\/review$/)
    await card('agent takes back').getByRole('button', { name: 'Reject the entry at 11:00 PM' }).click()
    expect((await reviewed).status()).toBe(404)
    await expect(page.getByRole('alertdialog').filter({ hasText: 'This entry is no longer in the batch. Review the other entries of the batch.' })).toBeVisible()
    await expect(card('agent takes back')).toHaveCount(0)
    await expect(counts).toHaveText('7 entries: 4 kept, 3 rejected')
    await expect(page.getByRole('heading', { level: 1, name, exact: true })).toBeFocused()
  })
  await shot('entry-gone', { fullPage: false, keepToast: true })

  const dialog = page.getByRole('alertdialog', { name: `Accept “${name}”?` })
  await step('Accept asks first, with the kept and the rejected counts', async () => {
    await page.getByRole('button', { name: 'Accept', exact: true }).click()
    await expect(dialog).toContainText('4 entries go into your journal, and 3 entries that you rejected stay out.')
    await settle()
  })
  await shot('accept-confirm', { fullPage: false })
  await asPhone('accept-confirm-phone', { fullPage: false })
  await asDark('accept-confirm-dark', { fullPage: false })

  await step('the agent puts one more entry while the dialog is open: the accept gets the 409, and the dialog shows the new counts', async () => {
    await agent('PUT', `/api/v1/import-batches/${batch.id}/entries`, {
      entries: [{ clientSessionId: e.late, date: '2024-03-05', position: 0, time: '10:00', raw: 'A late entry.' }],
    })
    const accepted = answered('POST', /\/accept$/)
    await dialog.getByRole('button', { name: 'Accept batch', exact: true }).click()
    expect((await accepted).status()).toBe(409)
    await expect(dialog.getByRole('status')).toHaveText('The batch changed. Recall read it again. Check the new counts, then accept the batch.')
    await expect(dialog.getByRole('alert')).toHaveCount(0)
    await expect(dialog).toContainText('5 entries go into your journal, and 3 entries that you rejected stay out.')
    await settle()
  })
  await shot('accept-stale', { fullPage: false })
  await asPhone('accept-stale-phone', { fullPage: false })

  const result = await step('Accept batch: the page shows the counts of the result, with a link to the journal', async () => {
    const accepted = answered('POST', /\/accept$/)
    await dialog.getByRole('button', { name: 'Accept batch', exact: true }).click()
    const res = await accepted
    expect(res.status()).toBe(200)
    const body = await res.json()
    expect(body.result).toEqual({ sessionsMade: 5, entriesRejected: 3, daysRejected: 1, skippedInJournal: 0, skippedDeleted: 0 })
    const list = page.getByRole('list', { name: 'Result of the accept' })
    await expect(list.getByRole('listitem')).toHaveText([
      '5 entries made in your journal',
      '3 entries left out because you rejected them or their day',
      '1 day that you rejected',
      '0 entries skipped because your journal has them',
      '0 entries skipped because you deleted them from your journal',
    ])
    return body
  })
  await shot('accept-result')
  await asPhone('accept-result-phone')
  await asDark('accept-result-dark')

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
  await shot('journal')

  await step('Settings: the import review says who can accept, and a change sends it', async () => {
    await page.getByRole('navigation', { name: 'Main' }).getByRole('link', { name: 'Settings', exact: true }).click()
    const box = page.getByRole('checkbox', { name: 'Only I can accept an import batch' })
    await expect(box).toBeChecked()
    await expect(page.getByText(/^With this on, only you can accept an import batch, and only in Recall in a browser\./)).toBeVisible()
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
  await shot('setting')
  await asPhone('setting-phone')

  const second = await step('a batch of 300 entries on two days has two pages, and the pager names the dates of each', async () => {
    const opened = await agent('POST', '/api/v1/import-batches', { name: 'Notes to throw away', timeZone: 'Europe/Berlin' })
    const entries = ['2023-07-01', '2023-07-02'].flatMap((date) =>
      Array.from({ length: 150 }, (_, position) => ({ clientSessionId: id(), date, position, raw: `Note ${position + 1} of ${date}.` })),
    )
    expect((await agent('PUT', `/api/v1/import-batches/${opened.body.id}/entries`, { entries })).status).toBe(200)
    const listed = answered('GET', /^\/api\/v1\/import-batches$/)
    await page.getByRole('navigation', { name: 'Main' }).getByRole('link', { name: 'Imports', exact: true }).click()
    const shown = (await (await listed).json()).find((b) => b.id === opened.body.id)
    await page.getByRole('link', { name: shown.name, exact: true }).click()
    const pager = page.getByRole('navigation', { name: 'Pages of this batch', exact: true })
    await expect(pager).toContainText('Page 1 of 2: Jul 1, 2023')
    await expect(page.getByLabel('Counts of this batch')).toHaveText('300 entries: 300 kept, 0 rejected')
    return shown
  })
  await shot('pager', { fullPage: false })
  await asPhone('pager-phone', { fullPage: false })

  await step('Discard asks first with the quoted name and the count of the days that the page read', async () => {
    await page.getByRole('navigation', { name: 'Main' }).getByRole('link', { name: 'Imports', exact: true }).click()
    await page.getByRole('button', { name: `Discard ${second.name}` }).click()
    const confirm = page.getByRole('alertdialog', { name: `Discard “${second.name}”?` })
    await expect(confirm).toContainText('Discard deletes this batch and its 300 entries. None of them go into your journal. You cannot undo a discard.')
    await settle()
  })
  await shot('discard-confirm', { fullPage: false })
  await asPhone('discard-confirm-phone', { fullPage: false })

  await step('Discard batch: the batch leaves the list, and the accepted batch stays with no Discard', async () => {
    const discarded = answered('DELETE', /^\/api\/v1\/import-batches\/[^/]+$/)
    await page.getByRole('alertdialog').getByRole('button', { name: 'Discard batch', exact: true }).click()
    expect((await discarded).status()).toBe(204)
    await expect(page.getByRole('link', { name: second.name })).toHaveCount(0)
    await expect(row()).toContainText('In your journal')
    await expect(row().getByRole('button')).toHaveCount(0)
  })
  await shot('list-after-discard')

  await step('a batch that does not exist says so, with a link to the imports', async () => {
    await page.goto(`/imports/${second.id}`)
    await expect(page.getByRole('heading', { level: 1, name: 'Batch not found' })).toBeVisible()
    await expect(page.getByText('This import batch does not exist. Pick a batch from your imports.')).toBeVisible()
    await expect(page.getByRole('link', { name: 'Go to your imports' })).toHaveAttribute('href', '/imports')
  })
  await shot('missing')
  await asPhone('missing-phone')
  await asDark('missing-dark')

  const openBatchAt = async (batchName) => {
    const opened = await agent('POST', '/api/v1/import-batches', { name: batchName, timeZone: 'America/New_York' })
    expect(opened.status).toBe(201)
    return opened.body
  }
  const showBatch = async (batchId) => {
    const days = answered('GET', /^\/api\/v1\/import-batches\/[^/]+\/days$/)
    await page.goto(`/imports/${batchId}`)
    expect((await days).status()).toBe(200)
  }
  const accept = page.getByRole('button', { name: 'Accept', exact: true })

  await step('a batch with no kept entry: Accept is off, and the page says to keep an entry or to discard the batch', async () => {
    const left = await openBatchAt('Notes to leave out')
    const put = await agent('PUT', `/api/v1/import-batches/${left.id}/entries`, {
      entries: [
        { clientSessionId: id(), date: '2022-05-10', position: 0, time: '07:00', raw: 'A test line.' },
        { clientSessionId: id(), date: '2022-05-10', position: 1, raw: 'Another test line.' },
      ],
    })
    expect(put.status).toBe(200)
    await showBatch(left.id)
    const reviewed = answered('PUT', /\/days\/review$/)
    await page.getByRole('button', { name: 'Reject Tuesday, May 10, 2022' }).click()
    expect((await reviewed).status()).toBe(200)
    await expect(page.getByLabel('Counts of this batch')).toHaveText('2 entries: 0 kept, 2 rejected')
    await expect(accept).toBeDisabled()
    await expect(accept).toHaveAccessibleDescription('Keep an entry to accept this batch, or discard it.')
    await expect(page.getByText('Keep an entry to accept this batch, or discard it.')).toBeVisible()
  })
  await shot('nothing-kept')

  await step('a batch with no entry: Accept is off, and the page says to wait for the agent or to discard the batch', async () => {
    const empty = await openBatchAt('Batch with no entries')
    await showBatch(empty.id)
    await expect(page.getByLabel('Counts of this batch')).toHaveText('0 entries: 0 kept, 0 rejected')
    await expect(accept).toBeDisabled()
    await expect(accept).toHaveAccessibleDescription('Wait for the agent to add entries, or discard this batch.')
    await expect(page.getByText('This batch has no entries yet. The agent adds them, then you review them here.')).toBeVisible()
  })
  await shot('empty-batch')
}
