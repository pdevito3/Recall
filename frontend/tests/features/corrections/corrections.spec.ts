import { aCorrection, correctionsApi, correctionsPaths } from '@tests/support/api/corrections-api'
import { expect, test } from '@tests/support/fixtures'
import { correctionsPage } from './corrections.tasks'

test.describe('the corrections of a journal', () => {
  test('lists each correction with its mishearings, and a known word as a pill', async ({ page, api }) => {
    api.mock(correctionsApi.listIs([aCorrection('cor_1', 'GRDB', ['grid b', 'great db'], true), aCorrection('cor_2', 'Kubernetes')]))
    const corrections = correctionsPage(page)

    await corrections.open()

    await expect(corrections.at.term('GRDB')).toBeVisible()
    await expect(corrections.at.heardAs('grid b, great db')).toBeVisible()
    await expect(corrections.at.alwaysReplaced).toHaveCount(1)
    await expect(corrections.at.knownWords).toHaveText(['Kubernetes'])
    await expect(corrections.at.term('Kubernetes')).toHaveCount(0)
    expect(api.last('get', correctionsPaths.list)?.query.get('pageSize')).toBe('100')
  })

  test('lists the terms in alphabetical order with no regard to case', async ({ page, api }) => {
    api.mock(correctionsApi.listIs([aCorrection('cor_1', 'Zoë', ['zoey']), aCorrection('cor_2', 'pnpm', ['p npm']), aCorrection('cor_3', 'GRDB', ['grid b'])]))
    const corrections = correctionsPage(page)

    await corrections.open()

    await expect(corrections.at.rows).toHaveText([/^GRDB/, /^pnpm/, /^Zoë/])
  })

  test('says that the list is empty', async ({ page, api }) => {
    api.mock(correctionsApi.listIs([]))
    const corrections = correctionsPage(page)

    await corrections.open()

    await expect(corrections.at.empty).toBeVisible()
  })

  test('a search with a quote finds a mishearing and says when none matches', async ({ page, api }) => {
    api.mock(correctionsApi.listIs([aCorrection('cor_1', 'GRDB', ['"grid" b']), aCorrection('cor_2', 'Kubernetes', ['cooper netties'])]))
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

    for (const button of [corrections.at.edit(term), corrections.at.remove(term)]) {
      const box = (await button.boundingBox())!
      expect(box.x).toBeGreaterThanOrEqual(0)
      expect(box.x + box.width).toBeLessThanOrEqual(390)
    }
    expect(await corrections.sideScrollRange()).toBe(0)
  })

  test('on a phone, always replace takes a 44 px tap and keeps its hint under it', async ({ page, api }) => {
    await page.setViewportSize({ width: 390, height: 844 })
    api.mock(correctionsApi.listIs([]))
    const corrections = correctionsPage(page)
    await corrections.open()
    await corrections.at.add.click()

    const label = await corrections.alwaysReplaceLayout()
    expect(label.gapAboveHint).toBeLessThanOrEqual(8)
    await page.mouse.click(label.x, label.y - 21)
    await expect(corrections.at.alwaysReplace).toBeChecked()
    await page.mouse.click(label.x, label.y + 21)
    await expect(corrections.at.alwaysReplace).not.toBeChecked()
  })

  test('on a phone, the × of a known word takes a 44 px tap that does not reach the next pill', async ({ page, api }) => {
    await page.setViewportSize({ width: 390, height: 844 })
    const words = [aCorrection('cor_1', 'GRDB'), aCorrection('cor_2', 'Radancy'), aCorrection('cor_3', 'Tallinn'), aCorrection('cor_4', 'Zoë')]
    api.mock(correctionsApi.listIs(words))
    const corrections = correctionsPage(page)
    await corrections.open()

    expect(await corrections.closeReachIntoNextPill()).toBeLessThan(0)
    for (const [index, [dx, dy]] of [[-21, 0], [21, 0], [0, -21], [0, 21]].entries()) {
      const word = words[index]
      const close = await corrections.centerOf(corrections.at.removeKnownWord(word.canonicalTerm))
      api.mock(correctionsApi.deletes(word.id), correctionsApi.listIs(words.slice(index + 1)))
      await page.mouse.click(close.x + dx, close.y + dy)
      await expect(corrections.at.removeKnownWord(word.canonicalTerm)).toHaveCount(0)
    }
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

    await corrections.add('GRDB', ['grid b'])

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
    await expect(corrections.at.alwaysReplaced).toBeVisible()
  })

  test('deletes a correction after a confirm, and puts the focus on the heading', async ({ page, api }) => {
    api.mock(correctionsApi.listIs([aCorrection('cor_1', 'GRDB', ['grid b']), aCorrection('cor_2', 'Kubernetes', ['cooper netties'])]))
    const corrections = correctionsPage(page)
    await corrections.open()

    api.mock(correctionsApi.deletes('cor_1'), correctionsApi.listIs([aCorrection('cor_2', 'Kubernetes', ['cooper netties'])]))
    await corrections.remove('cor_1', 'GRDB')

    await expect(corrections.at.term('GRDB')).toHaveCount(0)
    await expect(corrections.at.toast('Correction deleted')).toBeVisible()
    expect(await corrections.at.toasts.count()).toBe(1)
    await expect(corrections.at.heading).toBeFocused()
    expect(api.last('delete', correctionsPaths.one('cor_1'))?.query.get('ifVersion')).toBe('1')
  })

  test('keeps a correction that changed on another device after the page loaded it, and shows the change', async ({ page, api }) => {
    api.mock(correctionsApi.listIs([aCorrection('cor_1', 'GRDB', ['grid b'])]))
    const corrections = correctionsPage(page)
    await corrections.open()

    api.mock(correctionsApi.deleteIsStale('cor_1'), correctionsApi.listIs([{ ...aCorrection('cor_1', 'GRDB', ['girdb']), version: 2 }]))
    await corrections.remove('cor_1', 'GRDB')

    await expect(corrections.at.toast('This record changed after you loaded it.')).toBeVisible()
    await expect(corrections.at.term('GRDB')).toBeVisible()
    await expect(corrections.at.heardAs('girdb')).toBeVisible()
    expect(api.last('delete', correctionsPaths.one('cor_1'))?.query.get('ifVersion')).toBe('1')
  })

  test('Enter adds a known word, and its × removes it with no confirm', async ({ page, api }) => {
    const radancy = aCorrection('cor_4', 'Radancy')
    api.mock(correctionsApi.listIs([]))
    const corrections = correctionsPage(page)
    await corrections.open()

    api.mock(correctionsApi.adds(radancy), correctionsApi.listIs([radancy]))
    await corrections.addKnownWord(' Radancy ')

    expect(api.last('post', correctionsPaths.list)?.body).toEqual({ canonicalTerm: 'Radancy', mishearings: [], hardReplace: false })
    await expect(corrections.at.knownWords).toHaveText(['Radancy'])
    await expect(corrections.at.knownWordInput).toHaveValue('')
    await expect(corrections.at.knownWordInput).toBeFocused()
    await expect(corrections.at.empty).toBeVisible()

    api.mock(correctionsApi.deletes('cor_4'), correctionsApi.listIs([]))
    await corrections.removeKnownWord('cor_4', 'Radancy')

    await expect(corrections.at.knownWords).toHaveCount(0)
    await expect(corrections.at.knownWordInput).toBeFocused()
    expect(api.last('delete', correctionsPaths.one('cor_4'))?.query.get('ifVersion')).toBe('1')
    expect(await corrections.at.toasts.count()).toBe(0)
  })
})
