import type { Page } from '@playwright/test'

export const about = (page: Page) => ({
  supportBundleButton: page.getByRole('button', { name: 'Download Support Bundle' }),
  supportHeading: page.getByRole('heading', { level: 2, name: 'Support' }),
  text: page.getByText('Recall is your private journal.'),
  refusal: (detail: string) => page.getByRole('region', { name: /notification/ }).getByText(detail),
})
