import { spawn } from 'node:child_process'
import { randomUUID } from 'node:crypto'
import { createServer } from 'node:net'
import { fileURLToPath } from 'node:url'

const fakeBinary = fileURLToPath(new URL('../../../../tests/Recall.FakeModelProvider/bin/Debug/net10.0/Recall.FakeModelProvider.dll', import.meta.url))

const freePort = () =>
  new Promise((resolve, reject) => {
    const probe = createServer()
    probe.once('error', reject)
    probe.listen(0, '127.0.0.1', () => {
      const { port } = probe.address()
      probe.close(() => resolve(port))
    })
  })

async function startFake(key) {
  const port = await freePort()
  const url = `http://127.0.0.1:${port}`
  const child = spawn('dotnet', [fakeBinary, url], { env: { ...process.env, FAKE_PROVIDER_KEY: key }, stdio: 'ignore' })
  for (let tries = 0; tries < 100; tries++) {
    const answered = await fetch(`${url}/control`).then((r) => r.ok, () => false)
    if (answered) return { url, stop: () => child.kill('SIGTERM') }
    await new Promise((resolve) => setTimeout(resolve, 100))
  }
  child.kill('SIGTERM')
  throw new Error('The fake did not start. Build the solution first.')
}

export default async ({ page, step, capture, expect, login }) => {
  const key = `fake-key-${randomUUID()}`
  const fake = await startFake(key)
  const name = `Fake ${Date.now().toString(36)}`
  const noModelHint = 'A Cleanup does not use this model provider. Pick a model, then make it active.'
  const withModelHint = 'A Cleanup does not use this model provider until you make it active.'
  // A sweep gives each name in an answer a long value, so a card is found by the id of its provider.
  const cardOf = ({ id }) => page.locator('[data-slot="card"]').filter({ has: page.locator(`[id="new-key-${id}"]`) })
  const activeBadge = (provider) => cardOf(provider).getByText('Active', { exact: true })

  const connect = async (title, apiKey) => {
    await page.getByLabel('Name').fill(title)
    await page.getByLabel('Base URL').fill(fake.url)
    await page.getByLabel('API key', { exact: true }).fill(apiKey)
    const added = page.waitForResponse((r) => r.request().method() === 'POST' && new URL(r.url()).pathname === '/api/v1/model-providers')
    await page.getByRole('button', { name: 'Connect', exact: true }).click()
    const { id, name: shown } = await (await added).json()
    await expect(page.getByRole('heading', { level: 3, name: shown })).toBeVisible()
    await expect(page.getByLabel('API key', { exact: true })).toHaveValue('')
    return { id, name: shown }
  }

  const testAndPickModel = async (provider) => {
    const card = cardOf(provider)
    await card.getByRole('button', { name: 'Test connection' }).click()
    await expect(card.getByRole('status').filter({ hasText: 'Connected.' })).toBeVisible()
    await card.getByRole('button', { name: /Model of / }).click()
    const saved = page.waitForResponse((r) => r.request().method() === 'PUT' && new URL(r.url()).pathname === `/api/v1/model-providers/${provider.id}`)
    await page.getByRole('option').first().click()
    expect((await saved).status()).toBe(200)
    await expect(card.getByText('Test the connection, then pick a model.', { exact: true })).toHaveCount(0)
  }

  // A sweep gives the name a new value in each answer, so only the list tells which provider is active.
  const readActive = () =>
    page.evaluate(async () => {
      const headers = { 'X-CSRF': '1' }
      const list = await (await fetch('/api/v1/model-providers', { headers })).json()
      const active = await (await fetch('/api/v1/model-providers/active', { headers })).json()
      return { ids: list.items.filter((p) => p.isActive).map((p) => p.id), named: active.name !== null }
    })

  try {
    await step('log in as the Owner, an Admin of the tenant of a new database', async () => {
      await login()
    })

    const first = await step('Model providers in the sidebar: connect the first provider of the tenant, and it is not active', async () => {
      await page.goto('/')
      await page.getByRole('link', { name: 'Model providers', exact: true }).click()
      await expect(page.getByRole('heading', { level: 1, name: 'Model providers' })).toBeVisible()
      const provider = await connect(name, key)
      await expect(cardOf(provider).getByText(noModelHint, { exact: true })).toBeVisible()
      await expect(activeBadge(provider)).toHaveCount(0)
      return provider
    })
    await capture('connected')

    await step('Test connection and a pick of a model: the provider has a model and is still not active', async () => {
      await testAndPickModel(first)
      await expect(cardOf(first).getByText(withModelHint, { exact: true })).toBeVisible()
      await expect(activeBadge(first)).toHaveCount(0)
      expect(await readActive()).toEqual({ ids: [], named: false })
    })
    await capture('tested')

    await step('Make active: the badge Active shows, and the button goes away', async () => {
      await cardOf(first).getByRole('button', { name: 'Make active' }).click()
      await expect(activeBadge(first)).toBeVisible()
      await expect(cardOf(first).getByRole('button', { name: 'Make active' })).toHaveCount(0)
      expect(await readActive()).toEqual({ ids: [first.id], named: true })
    })

    await step('a second provider with a model: a connect does not take the place of the active provider', async () => {
      const second = await connect(`${name} second`, key)
      await testAndPickModel(second)
      await expect(cardOf(second).getByText(withModelHint, { exact: true })).toBeVisible()
      await expect(activeBadge(second)).toHaveCount(0)
      await expect(activeBadge(first)).toBeVisible()
      expect(await readActive()).toEqual({ ids: [first.id], named: true })
    })
    await capture('second-provider')

    await step('a wrong key: the alert holds the words of the provider', async () => {
      const wrong = await connect(`${name} wrong`, `wrong-${randomUUID()}`)
      await cardOf(wrong).getByRole('button', { name: 'Test connection' }).click()
      await expect(cardOf(wrong).getByRole('alert')).toContainText('The connection failed.')
      await expect(cardOf(wrong).getByText(noModelHint, { exact: true })).toBeVisible()
    })
    await capture('bad-key')

    const session = await step('add a Session with a draft through the API', async () => {
      const made = await page.evaluate(async (clientSessionId) => {
        const me = await (await fetch('/auth/me', { headers: { 'X-CSRF': '1' } })).json()
        const headers = { 'Content-Type': 'application/json', 'X-CSRF': '1', 'X-Recall-User': me.sub }
        const post = await fetch('/api/v1/sessions', { method: 'POST', headers, body: JSON.stringify({ clientSessionId, startedAt: new Date().toISOString() }) })
        const created = await post.json()
        const text = JSON.stringify({ type: 'doc', content: [{ type: 'paragraph', content: [{ type: 'text', text: 'um I walked the dog' }] }] })
        const saved = await fetch(`/api/v1/sessions/${created.id}/draft`, { method: 'PUT', headers, body: JSON.stringify({ text, baseRevision: 0, savedAt: new Date().toISOString() }) })
        return { status: saved.status, id: created.id }
      }, randomUUID())
      expect(made.status).toBe(200)
      return made
    })

    const cleanup = page.getByRole('region', { name: 'Cleanup', exact: true })

    await step('Run Cleanup on the entry page: the page says that the person can leave', async () => {
      await page.goto(`/sessions/${session.id}`)
      await cleanup.getByRole('button', { name: /^Run Cleanup/ }).click()
      await expect(cleanup.getByRole('status').filter({ hasText: /You can close this page|Cleaned is current/ })).toBeVisible()
    })
    await capture('running')

    await step('the page shows the result with no reload, in the Cleaned tab', async () => {
      await expect(cleanup.getByRole('status').filter({ hasText: 'Cleaned is current.' })).toBeVisible({ timeout: 60_000 })
      await page.getByRole('tab', { name: 'Cleaned', exact: true }).click()
      await expect(page.getByRole('textbox', { name: 'Cleaned version' })).not.toBeEmpty()
    })
    await capture('cleaned')

    await step('a later visit shows Cleaned, and Your words keep the words of the draft', async () => {
      await page.reload()
      await expect(cleanup.getByRole('status').filter({ hasText: 'Cleaned is current.' })).toBeVisible()
      await page.getByRole('tab', { name: 'Your words', exact: true }).click()
      await expect(page.getByRole('textbox', { name: 'Your words' })).toContainText('um I walked the dog')
    })
  } finally {
    fake.stop()
  }
}
