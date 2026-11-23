import { aPerson, peopleApi, peoplePaths } from '@tests/support/api/people-api'
import { expect, test } from '@tests/support/fixtures'
import { peoplePage } from './people.tasks'

test.describe('the people of a journal', () => {
  test('lists each person', async ({ page, api }) => {
    api.mock(peopleApi.listIs([aPerson('per_1', 'Ana Reyes'), aPerson('per_2', 'Ben Stone')]))
    const people = peoplePage(page)

    await people.open()

    await expect(people.at.person('Ana Reyes')).toBeVisible()
    await expect(people.at.person('Ben Stone')).toBeVisible()
  })

  test('a long name in another script stays in the page on a phone', async ({ page, api }) => {
    await page.setViewportSize({ width: 390, height: 844 })
    const name = '\u05e9'.repeat(100)
    api.mock(peopleApi.listIs([aPerson('per_1', name)]))
    const people = peoplePage(page)

    await people.open()

    await expect(people.at.person(name)).toHaveAttribute('dir', 'auto')
    await expect(people.at.rename(name)).toBeInViewport({ ratio: 1 })
    await expect(people.at.add).toBeInViewport({ ratio: 1 })
    expect(await people.sideScrollRange()).toBe(0)
  })

  test('says that the list is empty', async ({ page }) => {
    const people = peoplePage(page)

    await people.open()

    await expect(people.at.empty).toBeVisible()
  })

  test('adds a person and clears the field', async ({ page, api }) => {
    api.mock(peopleApi.adds(aPerson('per_3', 'Cara')))
    const people = peoplePage(page)
    await people.open()

    await people.add('Cara')

    expect(api.last('post', peoplePaths.list)?.body).toEqual({ label: 'Cara' })
    await expect(people.at.newPerson).toHaveValue('')
  })

  test('renames a person', async ({ page, api }) => {
    api.mock(peopleApi.listIs([aPerson('per_1', 'Ana Reyes')]), peopleApi.renames(aPerson('per_1', 'Ana R')))
    const people = peoplePage(page)
    await people.open()

    await people.rename('per_1', 'Ana Reyes', 'Ana R')

    expect(api.last('put', peoplePaths.one('per_1'))?.body).toEqual({ label: 'Ana R' })
    await expect(people.at.renameField).toHaveCount(0)
  })

  test('edits the details of a person, keeps the name, and lists the details under it', async ({ page, api }) => {
    const details = { fullName: 'Jane Park', notes: "Sam's teacher" }
    api.mock(peopleApi.listIs([aPerson('per_1', 'Jane')]))
    const people = peoplePage(page)
    await people.open()
    api.mock(peopleApi.listIs([aPerson('per_1', 'Jane', details)]), peopleApi.describes(aPerson('per_1', 'Jane', details)))

    await people.describe('per_1', 'Jane', details)

    expect(api.last('put', peoplePaths.details('per_1'))?.body).toEqual({ ...details, version: 1 })
    await expect(people.at.fullNameField).toHaveCount(0)
    await expect(people.at.person('Jane')).toBeVisible()
    await expect(people.at.person('Jane Park')).toBeVisible()
    await expect(people.at.person("Sam's teacher")).toBeVisible()
  })

  test('long details with no spaces stay in the page on a phone', async ({ page, api }) => {
    await page.setViewportSize({ width: 390, height: 844 })
    api.mock(peopleApi.listIs([aPerson('per_1', 'Jane', { fullName: 'J'.repeat(200), notes: 'n'.repeat(1000) })]))
    const people = peoplePage(page)

    await people.open()

    await expect(people.at.details('Jane')).toBeInViewport({ ratio: 1 })
    await expect(people.at.rename('Jane')).toBeInViewport({ ratio: 1 })
    expect(await people.sideScrollRange()).toBe(0)
  })
})
