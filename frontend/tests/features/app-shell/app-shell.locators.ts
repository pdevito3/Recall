import type { Page } from '@playwright/test'

export const appShell = (page: Page) => ({
  aboutHeading: page.getByRole('heading', { level: 1, name: 'About' }),
  aboutText: page.getByText('Recall is your private journal.', { exact: false }),
  currentPage: page.getByRole('navigation', { name: 'breadcrumb' }).getByRole('link'),
  contentInfoLandmarks: page.getByRole('contentinfo'),
  // The About page has no separator of its own, so the one separator in main is the one of the layout header.
  headerSeparator: page.getByRole('main').getByRole('separator'),
  routerDevtoolsButton: page.getByRole('button', { name: 'Open TanStack Router Devtools' }),
  mobileSidebar: page.locator('[data-slot="sidebar"][data-mobile="true"]'),
  mobileSidebarAppName: page.locator('[data-slot="sidebar"][data-mobile="true"]').getByText('Recall', { exact: true }),
  journalNavigationLink: page.getByRole('navigation', { name: 'Main' }).getByRole('link', { name: 'Journal' }),
  aboutNavigationLink: page.getByRole('navigation', { name: 'Main' }).getByRole('link', { name: 'About' }),
  currentNavigationItems: page.getByRole('navigation', { name: 'Main' }).locator('[aria-current]'),
  sidebarTrigger: page.getByRole('main').getByRole('button', { name: 'Toggle Sidebar' }),
  sidebarToggles: page.getByRole('button', { name: 'Toggle Sidebar' }),
  sidebarRail: page.locator('[data-slot="sidebar-rail"]'),
  pageHeading: page.getByRole('main').getByRole('heading', { level: 1 }),
  mainLandmarks: page.getByRole('main'),
  wideContent: page.getByRole('region', { name: 'Wide content' }),
  notFoundHeading: page.getByRole('heading', { name: 'Page not found' }),
  homeLink: page.getByRole('link', { name: 'Go to the home page' }),
  composerHint: page.getByText('Type @ to tag a person', { exact: true }),
  pageAnnouncement: page.getByTestId('page-announcement'),
  fileStoreNotice: page.getByRole('alert').filter({ hasText: 'Recall cannot write to its file store' }),
})
