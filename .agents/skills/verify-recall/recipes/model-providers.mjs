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
  const cardOf = (title) => page.locator('[data-slot="card"]').filter({ has: page.getByRole('heading', { level: 3, name: title }) })

  const connect = async (title, apiKey) => {
    await page.getByLabel('Name').fill(title)
    await page.getByLabel('Base URL').fill(fake.url)
    await page.getByLabel('API key', { exact: true }).fill(apiKey)
    await page.getByRole('button', { name: 'Connect', exact: true }).click()
    await expect(page.getByRole('heading', { level: 3, name: title })).toBeVisible()
    await expect(page.getByLabel('API key', { exact: true })).toHaveValue('')
  }

  try {
    await step('log in as the Owner, an Admin of the tenant of a new database', async () => {
      await login()
    })

    await step('Model providers in the sidebar: connect a provider with a key', async () => {
      await page.goto('/')
      await page.getByRole('link', { name: 'Model providers', exact: true }).click()
      await expect(page.getByRole('heading', { level: 1, name: 'Model providers' })).toBeVisible()
      await connect(name, key)
    })
    await capture('connected')

    await step('Test connection: the status names the models, and a pick saves a model', async () => {
      const card = cardOf(name)
      await card.getByRole('button', { name: 'Test connection' }).click()
      await expect(card.getByRole('status').filter({ hasText: 'Connected.' })).toBeVisible()
      await card.getByRole('button', { name: new RegExp(`Model of ${name}`) }).click()
      const saved = page.waitForResponse((r) => r.request().method() === 'PUT' && r.url().includes('/api/v1/model-providers/'))
      await page.getByRole('option').first().click()
      expect((await saved).status()).toBe(200)
      await expect(card.getByText('Test the connection, then pick a model.')).toHaveCount(0)
    })
    await capture('tested')

    await step('a wrong key: the alert holds the words of the provider', async () => {
      const wrong = `${name} wrong`
      await connect(wrong, `wrong-${randomUUID()}`)
      const card = cardOf(wrong)
      await card.getByRole('button', { name: 'Test connection' }).click()
      await expect(card.getByRole('alert')).toContainText('The connection failed.')
    })
    await capture('bad-key')

    const session = await step('add a Session with a draft through the API', async () => {
      const made = await page.evaluate(async (clientSessionId) => {
        const headers = { 'Content-Type': 'application/json', 'X-CSRF': '1' }
        const post = await fetch('/api/v1/sessions', { method: 'POST', headers, body: JSON.stringify({ clientSessionId, startedAt: new Date().toISOString() }) })
        const created = await post.json()
        const text = JSON.stringify({ type: 'doc', content: [{ type: 'paragraph', content: [{ type: 'text', text: 'um I walked the dog' }] }] })
        const saved = await fetch(`/api/v1/sessions/${created.id}/draft`, { method: 'PUT', headers, body: JSON.stringify({ text, baseRevision: 0, savedAt: new Date().toISOString() }) })
        return { status: saved.status, id: created.id }
      }, randomUUID())
      expect(made.status).toBe(200)
      return made
    })

    await step('Run Cleanup: the page says that the person can leave', async () => {
      await page.goto(`/model-providers?session=${session.id}`)
      await page.getByRole('button', { name: /^Run Cleanup/ }).click()
      await expect(page.getByRole('status').filter({ hasText: /You can close this page|Cleaned is current/ })).toBeVisible()
    })
    await capture('running')

    await step('a later visit shows Cleaned, and Raw has the words of the draft', async () => {
      await expect(async () => {
        await page.reload()
        await expect(page.getByRole('status').filter({ hasText: 'Cleaned is current.' })).toBeVisible({ timeout: 2000 })
      }).toPass({ timeout: 60_000 })
      const raw = page.locator('section', { has: page.getByRole('heading', { level: 3, name: 'Raw' }) })
      await expect(raw).toContainText('um I walked the dog')
    })
    await capture('cleaned')
  } finally {
    fake.stop()
  }
}
