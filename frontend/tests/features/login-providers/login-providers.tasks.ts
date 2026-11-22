import { expect, type Page } from '@playwright/test'
import { loginProviderPaths } from '@tests/support/api/login-providers-api'
import { loginProviders } from './login-providers.locators'

const answered = (page: Page, method: string, path: string) =>
  page.waitForResponse((r) => r.request().method() === method && new URL(r.url()).pathname === path)

export interface ProviderFields {
  id?: string
  displayName?: string
  issuer?: string
  clientId?: string
  clientSecret?: string
  caCertificatePath?: string
}

export function loginProvidersPage(page: Page) {
  const at = loginProviders(page)

  return {
    at,

    async open() {
      const listed = answered(page, 'GET', loginProviderPaths.list)
      await page.goto('/login-providers')
      await expect(at.heading).toBeVisible()
      await listed
    },

    async openAdd() {
      await at.add.click()
      await expect(at.idField).toBeVisible()
    },

    async openEdit(name: string) {
      await at.edit(name).click()
      await expect(at.saveEdit).toBeVisible()
    },

    async fill(fields: ProviderFields) {
      if (fields.id !== undefined) await at.idField.fill(fields.id)
      if (fields.displayName !== undefined) await at.nameField.fill(fields.displayName)
      if (fields.issuer !== undefined) await at.issuerField.fill(fields.issuer)
      if (fields.clientId !== undefined) await at.clientIdField.fill(fields.clientId)
      if (fields.clientSecret !== undefined) await at.clientSecretField.fill(fields.clientSecret)
      if (fields.caCertificatePath !== undefined) await at.caPathField.fill(fields.caCertificatePath)
    },

    async allowHttp() {
      // React Aria hides the input under its box, and in the tall dialog the sticky footer can cover the box, so press Space.
      await at.allowHttp.focus()
      await page.keyboard.press('Space')
      await expect(at.allowHttp).toBeChecked()
    },

    async add() {
      const sent = answered(page, 'POST', loginProviderPaths.list)
      await at.saveNew.click()
      await sent
    },

    async save(id: string) {
      const sent = answered(page, 'PUT', loginProviderPaths.record(id))
      await at.saveEdit.click()
      await sent
    },

    async loadLatest() {
      const read = answered(page, 'GET', loginProviderPaths.list)
      await at.loadLatest.click()
      await read
    },

    async test() {
      const sent = answered(page, 'POST', loginProviderPaths.test)
      await at.testIssuer.click()
      await sent
    },

    async delete(name: string, id: string) {
      await at.delete(name).click()
      const sent = answered(page, 'DELETE', loginProviderPaths.record(id))
      await at.confirmDelete.click()
      await sent
    },
  }
}
