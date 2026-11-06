import type { Page } from '@playwright/test'

export const membership = (page: Page) => {
  const notice = page.getByRole('region', { name: 'Membership check' })

  return {
    notice,
    tryAgain: notice.getByRole('button', { name: 'Try Again' }),
  }
}
