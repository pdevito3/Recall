import { expect, type Download, type Page } from '@playwright/test'
import { membershipPaths } from '@tests/support/api/membership-api'
import { actorSubject } from '@tests/support/auth/actors'
import { appShellPage } from '../app-shell/app-shell.tasks'
import { homePage } from '../home/home.tasks'
import { about } from './about.locators'

export function aboutPage(page: Page) {
  const at = about(page)

  return {
    at,

    async open() {
      await page.goto('/about')
      await expect(at.text).toBeVisible()
    },

    /** The route changes with no reload, so the page renders with the answer that the home page already has. */
    async openFromHomeWithTheMembershipLoaded() {
      const membershipLoaded = page.waitForResponse((response) => response.url().endsWith(membershipPaths.userOf(actorSubject)))
      await homePage(page).open()
      await membershipLoaded
      await appShellPage(page).at.aboutNavigationLink.click()
      await expect(at.text).toBeVisible()
    },

    async downloadSupportBundle(): Promise<Download> {
      const download = page.waitForEvent('download')
      await at.supportBundleButton.click()
      return download
    },
  }
}
