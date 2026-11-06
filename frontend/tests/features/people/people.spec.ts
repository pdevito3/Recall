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
})
