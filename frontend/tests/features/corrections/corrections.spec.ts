import { aCorrection, correctionsApi, correctionsPaths } from '@tests/support/api/corrections-api'
import { expect, test } from '@tests/support/fixtures'
import { correctionsPage } from './corrections.tasks'

test.describe('the corrections of a journal', () => {
  test('lists each correction with its mishearings', async ({ page, api }) => {
    api.mock(correctionsApi.listIs([aCorrection('cor_1', 'GRDB', ['grid b', 'great db'], true), aCorrection('cor_2', 'Kubernetes')]))
    const corrections = correctionsPage(page)

    await corrections.open()

    await expect(corrections.at.term('GRDB')).toBeVisible()
    await expect(corrections.at.list.getByText('Heard as grid b, great db', { exact: true })).toBeVisible()
    await expect(corrections.at.list.getByText('Always replaced', { exact: true })).toHaveCount(1)
    await expect(corrections.at.term('Kubernetes')).toBeVisible()
    expect(api.last('get', correctionsPaths.list)?.query.get('pageSize')).toBe('100')
  })

  test('says that the list is empty', async ({ page, api }) => {
    api.mock(correctionsApi.listIs([]))
    const corrections = correctionsPage(page)

    await corrections.open()

    await expect(corrections.at.empty).toBeVisible()
  })

  test('a search with a quote finds a mishearing and says when none matches', async ({ page, api }) => {
    api.mock(correctionsApi.listIs([aCorrection('cor_1', 'GRDB', ['"grid" b']), aCorrection('cor_2', 'Kubernetes')]))
    const corrections = correctionsPage(page)

    await corrections.open('"GRID"')

    await expect(corrections.at.term('GRDB')).toBeVisible()
    await expect(corrections.at.term('Kubernetes')).toHaveCount(0)
    await corrections.at.search.fill('nothing like it')
    await expect(corrections.at.noMatch('nothing like it')).toBeVisible()
    await expect.poll(() => new URL(page.url()).searchParams.get('search')).toBe('nothing like it')
  })

  test('a long term with no spaces stays in the page on a phone', async ({ page, api }) => {
    await page.setViewportSize({ width: 390, height: 844 })
    const term = 'W'.repeat(200)
    api.mock(correctionsApi.listIs([aCorrection('cor_1', term, ['m'.repeat(200)])]))
    const corrections = correctionsPage(page)

    await corrections.open()

    await corrections.at.remove(term).scrollIntoViewIfNeeded()
    await expect(corrections.at.edit(term)).toBeInViewport({ ratio: 1 })
    await expect(corrections.at.remove(term)).toBeInViewport({ ratio: 1 })
    expect(await corrections.sideScrollRange()).toBe(0)
  })

  test('adds a correction with its mishearings', async ({ page, api }) => {
    const added = aCorrection('cor_3', 'GRDB', ['grid b', 'great db'], true)
    api.mock(correctionsApi.listIs([]))
    const corrections = correctionsPage(page)
    await corrections.open()

    api.mock(correctionsApi.adds(added), correctionsApi.listIs([added]))
    await corrections.add(' GRDB ', ['grid b', '', 'great db'], true)

    expect(api.last('post', correctionsPaths.list)?.body).toEqual({
      canonicalTerm: 'GRDB',
      mishearings: ['grid b', 'great db'],
      hardReplace: true,
    })
    await expect(corrections.at.dialog).toHaveCount(0)
    await expect(corrections.at.term('GRDB')).toBeVisible()
    await expect(corrections.at.toast('Correction saved')).toBeVisible()
    expect(await corrections.at.toasts.count()).toBe(1)
  })

  test('a refused add shows the reason on its field, with no toast', async ({ page, api }) => {
    api.mock(correctionsApi.listIs([]), correctionsApi.addRefuses('canonicalTerm', 'The term has a character that is not allowed.'))
    const corrections = correctionsPage(page)
    await corrections.open()

    await corrections.add('GRDB', [])

    await expect(corrections.at.termError('The term has a character that is not allowed.')).toBeVisible()
    await expect(corrections.at.termField).toBeFocused()
    expect(await corrections.at.toasts.count()).toBe(0)
  })

  test('changes the mishearings and turns on always replace', async ({ page, api }) => {
    const grdb = aCorrection('cor_1', 'GRDB', ['grid b'])
    api.mock(correctionsApi.listIs([grdb]))
    const corrections = correctionsPage(page)
    await corrections.open()

    const saved = { ...grdb, mishearings: ['grid b', 'great db'], hardReplace: true, version: 2 }
    api.mock(correctionsApi.updates(saved), correctionsApi.listIs([saved]))
    await corrections.edit('cor_1', 'GRDB', { mishearings: ['grid b', 'great db'], alwaysReplace: true })

    expect(api.last('put', correctionsPaths.one('cor_1'))?.body).toEqual({
      canonicalTerm: 'GRDB',
      mishearings: ['grid b', 'great db'],
      hardReplace: true,
      version: 1,
    })
    await expect(corrections.at.dialog).toHaveCount(0)
    await expect(corrections.at.list.getByText('Always replaced', { exact: true })).toBeVisible()
  })

  test('deletes a correction after a confirm, and puts the focus on the heading', async ({ page, api }) => {
    api.mock(correctionsApi.listIs([aCorrection('cor_1', 'GRDB'), aCorrection('cor_2', 'Kubernetes')]))
    const corrections = correctionsPage(page)
    await corrections.open()

    api.mock(correctionsApi.deletes('cor_1'), correctionsApi.listIs([aCorrection('cor_2', 'Kubernetes')]))
    await corrections.remove('cor_1', 'GRDB')

    await expect(corrections.at.term('GRDB')).toHaveCount(0)
    await expect(corrections.at.toast('Correction deleted')).toBeVisible()
    expect(await corrections.at.toasts.count()).toBe(1)
    await expect(corrections.at.heading).toBeFocused()
  })
})
