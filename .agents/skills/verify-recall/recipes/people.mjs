const api = (page, method, path, body) => page.evaluate(async ({ method, path, body }) => {
  const me = await (await fetch('/auth/me', { headers: { 'X-CSRF': '1' } })).json()
  const response = await fetch(path, { method, headers: { 'X-CSRF': '1', 'X-Recall-User': me.sub, 'Content-Type': 'application/json' }, body: body ? JSON.stringify(body) : undefined })
  const text = await response.text()
  return { status: response.status, body: text ? JSON.parse(text) : null }
}, { method, path, body })

const isCall = (method, path) => (r) => r.request().method() === method && new URL(r.url()).pathname === path

export default async ({ page, step, capture, expect, login }) => {
  const list = page.getByRole('list', { name: 'Your people' })
  const dialog = page.getByRole('dialog')
  const longName = `Jane${'P'.repeat(80)}`

  await step('log in', async () => {
    await login()
  })

  const janes = await step('add two People named Jane', async () => {
    await page.goto('/people')
    const added = []
    for (let i = 0; i < 2; i++) {
      const answer = page.waitForResponse(isCall('POST', '/api/v1/people'))
      await page.getByRole('textbox', { name: 'Add a person' }).fill('Jane')
      await page.getByRole('button', { name: 'Add', exact: true }).click()
      const response = await answer
      expect(response.status()).toBe(201)
      added.push(await response.json())
      await expect(page.getByRole('textbox', { name: 'Add a person' })).toHaveValue('')
    }
    await expect(list.getByText('Jane', { exact: true })).toHaveCount(2)
    return added
  })

  await step('give each Jane details, and the list shows them under the name', async () => {
    const details = [
      { fullName: longName, notes: "Sam's teacher. We met at the school fair in the spring." },
      { fullName: 'Jane Doe', notes: '' },
    ]
    for (const [index, person] of janes.entries()) {
      await page.getByRole('button', { name: 'Edit the details of Jane', exact: true }).nth(index).click()
      await dialog.getByRole('textbox', { name: 'Full name' }).fill(details[index].fullName)
      await dialog.getByRole('textbox', { name: 'Notes' }).fill(details[index].notes)
      const answer = page.waitForResponse(isCall('PUT', `/api/v1/people/${person.id}/details`))
      await dialog.getByRole('button', { name: 'Save details', exact: true }).click()
      const saved = await (await answer).json()
      expect(saved.label).toBe('Jane')
      expect(saved.version).toBeGreaterThan(person.version)
      await expect(dialog).toHaveCount(0)
      await expect(list.getByText(saved.fullName, { exact: true })).toBeVisible()
    }
    await expect(list.getByText("Sam's teacher. We met at the school fair in the spring.", { exact: true })).toBeVisible()
  })
  await capture('people-with-details')

  await step('the details dialog opens with the saved details', async () => {
    await page.getByRole('button', { name: 'Edit the details of Jane', exact: true }).first().click()
    await expect(dialog.getByRole('textbox', { name: 'Full name' })).toHaveValue(longName)
  })
  await capture('details-dialog')
  await page.keyboard.press('Escape')

  const sessionId = await step('a device Cleanup that names Jane proposes both, and tags neither', async () => {
    const created = await api(page, 'POST', '/api/v1/sessions', { clientSessionId: crypto.randomUUID(), startedAt: new Date().toISOString() })
    await api(page, 'PUT', `/api/v1/sessions/${created.body.id}/draft`, { text: 'coffee with jane', baseRevision: 0, savedAt: new Date().toISOString() })
    const result = await api(page, 'POST', `/api/v1/sessions/${created.body.id}/cleanup/result`, {
      baseRawRevision: 1,
      baseCleanedRevision: 0,
      cleanedMarkdown: 'Coffee with Jane.',
      peopleProposal: ['Jane'],
    })
    expect(result.status).toBe(200)
    expect(result.body.peopleProposals[0].matchedPersonIds.sort()).toEqual(janes.map((j) => j.id).sort())
    expect(result.body.personIds).toEqual([])
    return created.body.id
  })

  await step('the entry shows the details of each Jane, and a Tag with no choice is not offered', async () => {
    await page.goto(`/sessions/${sessionId}`)
    const choices = page.getByRole('list', { name: 'People named Jane' })
    await expect(choices.getByText("Sam's teacher. We met at the school fair in the spring.", { exact: true })).toBeVisible()
    await expect(choices.getByText('Jane Doe', { exact: true })).toBeVisible()
    await expect(page.getByRole('button', { name: 'Tag Jane', exact: true })).toHaveCount(0)
    await choices.scrollIntoViewIfNeeded()
  })
  await capture('two-janes-proposal')

  await step('a Tag of Jane Doe tags that Jane', async () => {
    const answer = page.waitForResponse(isCall('POST', `/api/v1/sessions/${sessionId}/people-proposals/respond`))
    await page.getByRole('button', { name: 'Tag Jane Doe', exact: true }).click()
    const response = await answer
    expect(response.status()).toBe(200)
    const session = await response.json()
    expect(session.personIds).toEqual([janes[1].id])
    await expect(page.getByRole('list', { name: 'People named Jane' })).toHaveCount(0)
  })
  await capture('tagged-jane-doe')
}
