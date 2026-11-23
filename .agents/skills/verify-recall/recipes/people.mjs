const api = (page, method, path, body) => page.evaluate(async ({ method, path, body }) => {
  const me = await (await fetch('/auth/me', { headers: { 'X-CSRF': '1' } })).json()
  const response = await fetch(path, { method, headers: { 'X-CSRF': '1', 'X-Recall-User': me.sub, 'Content-Type': 'application/json' }, body: body ? JSON.stringify(body) : undefined })
  const text = await response.text()
  return { status: response.status, body: text ? JSON.parse(text) : null }
}, { method, path, body })

const isCall = (method, path) => (r) => r.request().method() === method && new URL(r.url()).pathname === path

const device = async (page, text, cleaned) => {
  const created = await api(page, 'POST', '/api/v1/sessions', { clientSessionId: crypto.randomUUID(), startedAt: new Date().toISOString() })
  await api(page, 'PUT', `/api/v1/sessions/${created.body.id}/draft`, { text, baseRevision: 0, savedAt: new Date().toISOString() })
  const result = await api(page, 'POST', `/api/v1/sessions/${created.body.id}/cleanup/result`, {
    baseRawRevision: 1,
    baseCleanedRevision: 0,
    cleanedMarkdown: cleaned,
    peopleProposal: ['Jane'],
  })
  if (result.status !== 200) throw new Error(`the device result got ${result.status}`)
  return result.body
}

// The sweep gives each name of an answer a long value, so the recipe reads each name from an answer, never from a literal.
export default async ({ page, step, capture, expect, login }) => {
  const list = page.getByRole('list', { name: 'Your people' })
  const dialog = page.getByRole('dialog')
  const longName = `Jane${'P'.repeat(80)}`
  const janes = []

  const addJane = async () => {
    await page.goto('/people')
    const answer = page.waitForResponse(isCall('POST', '/api/v1/people'))
    await page.getByRole('textbox', { name: 'Add a person' }).fill('Jane')
    await page.getByRole('button', { name: 'Add', exact: true }).click()
    const response = await answer
    expect(response.status()).toBe(201)
    janes.push(await response.json())
    await expect(page.getByRole('textbox', { name: 'Add a person' })).toHaveValue('')
    await expect(list.getByText(janes[0].label, { exact: true })).toHaveCount(janes.length)
  }

  await step('log in', async () => {
    await login()
  })

  const before = await step('a device Cleanup that names Jane before any Jane exists proposes her with no match', async () => {
    const session = await device(page, 'tea with jane', 'Tea with Jane.')
    expect(session.peopleProposals[0].matchedPersonIds).toEqual([])
    return session
  })

  const withOne = await step('add one Jane, and a device Cleanup that names Jane stores only her', async () => {
    await addJane()
    const session = await device(page, 'lunch with jane', 'Lunch with Jane.')
    expect(session.peopleProposals[0].matchedPersonIds).toEqual([janes[0].id])
    return session
  })

  const respondTo = (id) => page.waitForResponse(isCall('POST', `/api/v1/sessions/${id}/people-proposals/respond`))
  const label = janes[0].label

  const openEntry = await step('an entry with one Jane offers her', async () => {
    const session = await device(page, 'walk with jane', 'Walk with Jane.')
    await page.goto(`/sessions/${session.id}`)
    await expect(page.getByRole('button', { name: `Tag ${label}`, exact: true })).toBeVisible()
    await expect(page.getByRole('list', { name: `People named ${label}` })).toHaveCount(0)
    return session
  })
  await capture('one-jane-offered')

  await step('another device adds a second Jane while the entry is open, and the Tag still tags the Jane that the card shows', async () => {
    const added = await api(page, 'POST', '/api/v1/people', { label: 'Jane' })
    expect(added.status).toBe(201)
    janes.push(added.body)
    const answer = respondTo(openEntry.id)
    await page.getByRole('button', { name: `Tag ${label}`, exact: true }).click()
    const response = await answer
    expect(response.status()).toBe(200)
    expect((await response.json()).personIds).toEqual([janes[0].id])
    await expect(page.getByRole('button', { name: `Tag ${label}`, exact: true })).toHaveCount(0)
  })
  await capture('tagged-after-a-second-jane-came')

  const details = await step('give each Jane details, and the list shows them under the name', async () => {
    await page.goto('/people')
    await expect(list.getByText(label, { exact: true })).toHaveCount(2)
    const wanted = [
      { fullName: longName, notes: "Sam's teacher. We met at the school fair in the spring." },
      { fullName: 'Jane Doe', notes: '' },
    ]
    const saved = []
    for (const [index, person] of janes.entries()) {
      await page.getByRole('button', { name: `Edit the details of ${label}`, exact: true }).nth(index).click()
      await dialog.getByRole('textbox', { name: 'Full name' }).fill(wanted[index].fullName)
      await dialog.getByRole('textbox', { name: 'Notes' }).fill(wanted[index].notes)
      const answer = page.waitForResponse(isCall('PUT', `/api/v1/people/${person.id}/details`))
      await dialog.getByRole('button', { name: 'Save details', exact: true }).click()
      const response = await (await answer).json()
      expect(response.label).toBe(label)
      expect(response.version).toBeGreaterThan(person.version)
      await expect(dialog).toHaveCount(0)
      await expect(list.getByText(response.fullName, { exact: true })).toBeVisible()
      saved.push(response)
    }
    await expect(list.getByText(wanted[0].notes, { exact: true })).toBeVisible()
    return saved
  })
  await capture('people-with-details')

  await step('the details dialog opens with the saved details', async () => {
    await page.getByRole('button', { name: `Edit the details of ${label}`, exact: true }).first().click()
    await expect(dialog.getByRole('textbox', { name: 'Full name' })).toHaveValue(details[0].fullName)
  })
  await capture('details-dialog')
  await page.keyboard.press('Escape')

  const sessionId = await step('a device Cleanup that names Jane proposes both, and tags neither', async () => {
    const session = await device(page, 'coffee with jane', 'Coffee with Jane.')
    expect(session.peopleProposals[0].matchedPersonIds.sort()).toEqual(janes.map((j) => j.id).sort())
    expect(session.personIds).toEqual([])
    return session.id
  })

  const choices = page.getByRole('list', { name: `People named ${label}` })
  const tagOf = (index) => choices.getByRole('button', { name: `Tag ${details[index].fullName}`, exact: true })

  await step('the entry shows the details of each Jane, and a Tag with no choice is not offered', async () => {
    await page.goto(`/sessions/${sessionId}`)
    await expect(choices.getByText("Sam's teacher. We met at the school fair in the spring.", { exact: true })).toBeVisible()
    await expect(choices.getByText(details[1].fullName, { exact: true })).toBeVisible()
    await expect(page.getByRole('button', { name: `Tag ${label}`, exact: true })).toHaveCount(0)
    await choices.scrollIntoViewIfNeeded()
  })
  await capture('two-janes-proposal')

  await step('a Tag of Jane Doe tags that Jane', async () => {
    const answer = respondTo(sessionId)
    await tagOf(1).click()
    const response = await answer
    expect(response.status()).toBe(200)
    expect((await response.json()).personIds).toEqual([janes[1].id])
    await expect(choices).toHaveCount(0)
  })
  await capture('tagged-jane-doe')

  await step('the entry of the Cleanup from before the second Jane asks which, with its match first', async () => {
    await page.goto(`/sessions/${withOne.id}`)
    await expect(tagOf(0)).toBeVisible()
    await expect(tagOf(1)).toBeVisible()
    await expect(choices.getByRole('button', { name: /^Tag / }).first()).toHaveAccessibleName(`Tag ${details[0].fullName}`)
    await expect(page.getByRole('button', { name: `Tag ${label}`, exact: true })).toHaveCount(0)
    await choices.scrollIntoViewIfNeeded()
  })
  await capture('match-from-before-the-second-jane')

  await step('a Tag of the first Jane on that entry tags her', async () => {
    const answer = respondTo(withOne.id)
    await tagOf(0).click()
    const response = await answer
    expect(response.status()).toBe(200)
    expect((await response.json()).personIds).toEqual([janes[0].id])
    await expect(choices).toHaveCount(0)
  })
  await capture('tagged-the-first-jane')

  await step('the entry of the Cleanup from before any Jane asks which, and a Tag of Jane Doe tags her', async () => {
    await page.goto(`/sessions/${before.id}`)
    await expect(tagOf(0)).toBeVisible()
    await expect(page.getByRole('button', { name: `Tag ${label}`, exact: true })).toHaveCount(0)
    await choices.scrollIntoViewIfNeeded()
    await capture('no-match-from-before-any-jane')
    const answer = respondTo(before.id)
    await tagOf(1).click()
    const response = await answer
    expect(response.status()).toBe(200)
    expect((await response.json()).personIds).toEqual([janes[1].id])
    await expect(choices).toHaveCount(0)
  })

  await step('the @ picker of the editor shows the full name under each Jane', async () => {
    const editor = page.locator('.ProseMirror').first()
    await editor.click()
    await page.keyboard.press('End')
    await page.keyboard.type(` @${label}`)
    const picker = page.getByRole('listbox', { name: 'People' })
    await expect(picker.getByRole('option')).toHaveCount(2)
    await expect(picker.getByRole('option', { name: `${label} ${details[1].fullName}` })).toBeVisible()
  })
  await capture('mention-picker')
  await page.keyboard.press('Escape')
}
