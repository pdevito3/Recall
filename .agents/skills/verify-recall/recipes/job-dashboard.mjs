export default async ({ page, step, capture, settle, expect, login }) => {
  await step('log in as the Owner', () => login())

  await step('the Owner opens the console and the health job has finished', async () => {
    await expect(async () => {
      const response = await page.goto('/backwave/jobs')
      expect(response.status()).toBe(200)
      await expect(page.getByRole('cell', { name: 'system-health' }).first()).toBeVisible({ timeout: 1000 })
      await expect(page.getByText('Succeeded').first()).toBeVisible({ timeout: 1000 })
    }).toPass({ timeout: 30000 })
    await settle()
    await capture('owner-jobs')
  })

  await step('a request with no session gets a 403 with an empty body', async () => {
    const status = await page.evaluate(async () => {
      const response = await fetch('/backwave', { credentials: 'omit' })
      return { status: response.status, body: await response.text() }
    })
    expect(status).toEqual({ status: 403, body: '' })
  })
}
