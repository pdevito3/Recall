import { expect, type Page } from '@playwright/test'
import { peoplePaths } from '@tests/support/api/people-api'
import { people } from './people.locators'

export function peoplePage(page: Page) {
  const at = people(page)

  return {
    at,

    async open() {
      await page.goto('/people')
      await expect(at.heading).toBeVisible()
    },

    sideScrollRange: () => page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth),

    async add(name: string) {
      const added = page.waitForResponse((r) => r.request().method() === 'POST' && new URL(r.url()).pathname === peoplePaths.list)
      await at.newPerson.fill(name)
      await at.add.click()
      await added
    },

    async rename(id: string, from: string, to: string) {
      const renamed = page.waitForResponse((r) => r.request().method() === 'PUT' && new URL(r.url()).pathname === peoplePaths.one(id))
      await at.rename(from).click()
      await at.renameField.fill(to)
      await at.saveName.click()
      await renamed
    },

    async describe(id: string, name: string, details: { fullName: string; notes: string }) {
      const described = page.waitForResponse((r) => r.request().method() === 'PUT' && new URL(r.url()).pathname === peoplePaths.details(id))
      await at.details(name).click()
      await at.fullNameField.fill(details.fullName)
      await at.notesField.fill(details.notes)
      await at.saveDetails.click()
      await described
    },
  }
}
