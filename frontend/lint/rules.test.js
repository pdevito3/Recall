import { spawnSync } from 'node:child_process'
import { mkdirSync, mkdtempSync, readFileSync, rmSync, symlinkSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { dirname, join } from 'node:path'
import { afterAll, beforeAll, describe, expect, it } from 'vitest'
import { selectorMessages, testOnlyMessage } from './playwright.js'
import { browserZoneRules, codeToWordsRules, loginPageRules, wayOutRules } from './recall.js'

const frontend = join(import.meta.dirname, '..')

// Oxlint lints only files on disk, and each run loads the JS plugins, which takes about a second.
const cases = []
let diagnostics
const addCase = (code, path, config = 'app') => {
  const index = cases.push({ code, path, config }) - 1
  return () => diagnostics[index]
}
const messagesOf = (read) => read().map((diagnostic) => diagnostic.message)

// Oxlint reads options only from the root config, so the root of the run holds them.
const appConfig = JSON.parse(readFileSync(join(frontend, '.oxlintrc.json'), 'utf8').replace(/^\s*\/\/.*$/gm, ''))
const serverStateConfig = (path) => ({
  categories: { correctness: 'off' },
  jsPlugins: ['./lint/server-state.js'],
  rules: {
    'server-state/no-copied-state': 'error',
    'server-state/query-key-has-inputs': 'error',
    ...(path.endsWith('.keys.ts') && { 'server-state/key-has-each-param': 'error' }),
  },
})

const root = mkdtempSync(join(tmpdir(), 'recall-lint-'))
beforeAll(() => {
  cases.forEach(({ code, path, config }, index) => {
    const folder = join(root, String(index))
    mkdirSync(dirname(join(folder, path)), { recursive: true })
    writeFileSync(join(folder, path), code)
    symlinkSync(join(frontend, 'lint'), join(folder, 'lint'))
    if (config === 'app')
      writeFileSync(join(folder, '.oxlintrc.json'), JSON.stringify({ ...appConfig, options: undefined }))
    else
      writeFileSync(join(folder, '.oxlintrc.json'), JSON.stringify(serverStateConfig(path)))
  })
  writeFileSync(join(root, '.oxlintrc.json'), JSON.stringify({ options: appConfig.options, categories: { correctness: 'off' } }))
  const run = spawnSync(join(frontend, 'node_modules/.bin/oxlint'), ['--format', 'json', '--ignore-pattern', '*/lint/**', '.'], { cwd: root, encoding: 'utf8' })
  if (run.status > 1)
    throw new Error(run.stdout + run.stderr)
  diagnostics = cases.map(() => [])
  for (const diagnostic of JSON.parse(run.stdout).diagnostics.toSorted((a, b) => a.labels[0].span.offset - b.labels[0].span.offset))
    diagnostics[Number(diagnostic.filename.split('/')[0])].push({ rule: (diagnostic.code ?? 'unused-disable-directive').replace(/^[\w-]+\((.*)\)$/, '$1'), message: diagnostic.message })
})
afterAll(() => rmSync(root, { recursive: true, force: true }))

describe('playwright/no-selector-string', () => {
  const app = selectorMessages.app
  it.each([
    ['page.click(\'#save\')', 'tests/support/fixtures.ts', [app]],
    ['page.fill(\'#name\', \'Ada\')', 'tests/features/a/a.tasks.ts', [app]],
    ['frame.$(\'#save\')', 'tests/features/a/a.tasks.ts', [app]],
    ['page.waitForSelector(\'#save\')', 'tests/features/a/a.tasks.ts', [app]],
    ['page.click(\'#save\')', 'e2e/support/login.ts', [selectorMessages.stack]],
    ['save.click()', 'tests/features/a/a.tasks.ts', []],
    ['save.click({ force: true })', 'tests/features/a/a.tasks.ts', []],
    ['name.fill(\'Ada\')', 'tests/features/a/a.tasks.ts', []],
    ['name.fill(\'Ada\', { timeout: 1000 })', 'tests/features/a/a.tasks.ts', []],
    ['page.mouse.click(5, 5)', 'tests/features/a/a.tasks.ts', []],
    ['page.keyboard.press(\'Enter\')', 'tests/features/a/a.tasks.ts', []],
  ].map(([call, path, expected]) => [call, path, addCase(`export const act = () => ${call}\n`, path), expected]))('reads %s in %s', (_, __, read, expected) => {
    expect(messagesOf(read)).toEqual(expected)
  })
})

describe('the rules of a test file', () => {
  it.each([
    ['test.only(\'a\', () => {})', 'tests/features/a/a.spec.ts', 'no-test-only', testOnlyMessage],
    ['test.describe.only(\'a\', () => {})', 'e2e/a.spec.ts', 'no-test-only', testOnlyMessage],
    ['rows.nth(2)', 'tests/features/a/a.locators.ts', 'no-position'],
    ['rows.first()', 'tests/features/a/a.locators.ts', 'no-position'],
    ['page.waitForTimeout(100)', 'tests/features/a/a.tasks.ts', 'no-wait-for-timeout'],
    ['page.getByRole(\'button\')', 'tests/features/a/a.spec.ts', 'spec-no-locator'],
    ['page.waitForResponse(\'**/api\')', 'tests/features/a/a.spec.ts', 'spec-no-wait'],
    ['setTimeout(done, 100)', 'tests/features/a/a.spec.ts', 'spec-no-timer'],
  ].map(([code, path, rule, message]) => [code, path, rule, addCase(`${code}\n`, path), message]))('reports %s in %s with %s', (_, __, rule, read, message) => {
    expect(read().map((d) => d.rule)).toEqual([rule])
    if (message)
      expect(messagesOf(read)).toEqual([message])
  })

  const spec = addCase("import { expect, test } from '@playwright/test'\nimport { Link } from 'react-aria-components'\nexport { expect, test, Link }\n", 'tests/features/a/a.spec.ts')
  const locators = addCase("import { expect, test } from '@playwright/test'\nexport const at = (page) => page.getByRole('button')\nexport { expect, test }\n", 'tests/features/a/a.locators.ts')

  it('takes test and expect from the fixtures, and still refuses a Link, in a spec', () => {
    expect(spec().map((d) => d.rule)).toEqual(['spec-no-playwright-test', 'spec-no-playwright-test', 'no-restricted-imports'])
  })

  it('lets a locators file find an element and import from @playwright/test', () => {
    expect(locators()).toEqual([])
  })

  it.each([
    "import * as pw from '@playwright/test'\nexport const a = pw",
    "export { expect } from '@playwright/test'",
    "export * from '@playwright/test'",
  ].map((code) => [code, addCase(`${code}\n`, 'tests/features/a/a.spec.ts')]))('refuses %j in a spec', (_, read) => {
    expect(read().map((d) => d.rule)).toEqual(['spec-no-playwright-test'])
  })
})

describe('the rules of the app', () => {
  it.each([
    'export const a = (_b) => 1',
    'const [_a, b] = [1, 2]\nexport { b }',
    'const _x = 1\nexport {}',
    'export const f = <_U,>() => 1',
  ].map((code) => [code, addCase(`${code}\n`, 'src/lib/probe.ts')]))('reports the unused name in %j', (_, read) => {
    expect(read().map((d) => d.rule)).toEqual(['no-unused-vars'])
  })

  const unusedDisable = addCase('// eslint-disable-next-line @typescript-eslint/no-explicit-any\nexport const a = 1\n', 'src/lib/probe.ts')
  it('reports a disable comment that disables nothing', () => {
    expect(unusedDisable().map((d) => d.rule)).toEqual(['unused-disable-directive'])
  })

  const route = addCase("import { createFileRoute } from '@tanstack/react-router'\nexport const Route = createFileRoute('/a')({ component: Page })\nfunction Page() { return null }\nexport const loadThing = () => 1\n", 'src/routes/a.tsx')
  it('lets a route file export Route next to its component, and refuses another export', () => {
    expect(route().map((d) => d.rule)).toEqual(['only-export-components'])
    expect(messagesOf(route)[0]).toMatch(/share constants or functions/)
  })
})

describe('recall/import-order', () => {
  it.each([
    ["import { a } from './a'\nimport { useState } from 'react'", ['import-order']],
    ["import { a } from './a'\nimport { b } from '@/lib/b'", ['import-order']],
    ["import { b } from '@/lib/b'\nimport { useState } from 'react'", ['import-order']],
    ["import { useState } from 'react'\nimport { join } from 'node:path'", ['import-order']],
    ["import { join } from 'node:path'\nimport { useState } from 'react'\nimport { b } from '@/lib/b'\nimport { c } from '@tests/c'\nimport { a } from '../a'\nimport { d } from './d'", []],
    ["import { a } from './a'\nimport 'react-day-picker/style.css'\nimport { b } from './b'", []],
  ].map(([imports, expected]) => [imports, addCase(imports, 'src/lib/probe.ts'), expected]))('reads %j', (_, read, expected) => {
    expect(read().map((d) => d.rule).filter((rule) => rule === 'import-order')).toEqual(expected)
  })
})

const serverState = (read) => read().map((d) => `${d.rule}: ${d.message.split(',')[0]}`)
const serverStateCase = (code, path = 'src/domain/records/record.tsx') => addCase(code, path, 'server-state')

describe('server-state/no-copied-state', () => {
  const props = [
    serverStateCase('function Name(props) { const [name] = useState(props.record.name) }'),
    serverStateCase("function Name({ record }) { const [name] = useState(record?.name ?? '') }"),
  ]
  it('reports state that copies a prop', () => {
    expect(serverState(props[0])).toEqual(['no-copied-state: This state copies the prop `props`'])
    expect(serverState(props[1])).toEqual(['no-copied-state: This state copies the prop `record`'])
  })

  const localConst = serverStateCase('function Name({ id }) { const { data } = useRecord(id); const record = data!; const [name] = useState(record.name) }')
  it('reports state that copies a query result, also through a local const', () => {
    expect(serverState(localConst)).toEqual(['no-copied-state: This state copies the result of `useRecord`'])
  })

  const firstValues = serverStateCase(`function Name({ defaultOpen, initialText, props, value }) {
      const [open] = useState(defaultOpen)
      const [text] = useState(props.initialText ?? initialText)
      const [date] = useState(() => parseDate(value))
      const [count] = useState(0)
      const [shown] = useState(count)
    }`)
  it('takes a prop that names a first value, a calculated first value, and the value of other state', () => {
    expect(serverState(firstValues)).toEqual([])
  })

  const synced = serverStateCase(`function Search({ search }) {
      const [shown, setShown] = useState(search)
      if (search !== shown) setShown(search)
    }`)
  const inEffect = serverStateCase(`function Search({ search }) {
      const [shown, setShown] = useState(search)
      useEffect(() => { setShown(search) }, [search])
    }`)
  it('takes state that the render sets again from its source, and reports state that an effect sets again', () => {
    expect(serverState(synced)).toEqual([])
    expect(serverState(inEffect)).toEqual(['no-copied-state: This state copies the prop `search`'])
  })

  const localFirstValue = serverStateCase("function Name({ id }) { const { data } = useRecord(id); const initialName = data?.name ?? ''; const [name] = useState(initialName) }")
  it('takes a first-value name only from a prop, not from a local copy of a query result', () => {
    expect(serverState(localFirstValue)).toEqual(['no-copied-state: This state copies the result of `useRecord`'])
  })

  it.each([
    ['function A1(props) { const [name] = useState(props.record.name) }', 'the prop `props`'],
    ['function A2({ name }) { const [n] = useState(name) }', 'the prop `name`'],
    ["function A3({ id }) { const { data } = useRoomQ(id); const [n] = useState(data?.name ?? '') }", 'the result of `useRoomQ`'],
    ['function A4({ name }) { const copy = name; const [n] = useState(() => copy) }', 'the prop `name`'],
    ['function A5({ name }) { const [n, setN] = useState(name); useEffect(() => setN(name), [name]) }', 'the prop `name`'],
    ['function useCopy(value) { const [copy] = useState(value); return copy }', 'the argument `value`'],
    ['function A6({ id }) { const query = useRoomQ(id); const [n] = useState(query.data?.name) }', 'the result of `useRoomQ`'],
    ['function A7({ id }) { const { data: initialRecord } = useRoomQ(id); const [r] = useState(initialRecord) }', 'the result of `useRoomQ`'],
    ['function A8({ id }) { const initialRecord = useRoomQ(id).data; const [r] = useState(initialRecord) }', 'the result of `useRoomQ`'],
  ].map(([code, source]) => [code, serverStateCase(code), source]))('reports the probe %s', (_, read, source) => {
    expect(serverState(read)).toEqual([`no-copied-state: This state copies ${source}`])
  })

  it.each([
    'function B1({ first, last }) { const full = `${first} ${last}`; return full }',
    'function B3({ items }) { const [prevItems, setPrevItems] = useState(items); const [selection, setSelection] = useState(null); if (items !== prevItems) { setPrevItems(items); setSelection(null) } }',
    'function B4({ value }) { const [text, setText] = useState(value); const [prev, setPrev] = useState(value); if (value !== prev) { setPrev(value); setText(value) } }',
    'function B5({ initialCount }) { const [c] = useState(initialCount) }',
    'function B6(props) { const [o] = useState(props.defaultOpen ?? false) }',
    `function useDebouncedValue(value, delay = 300) {
      const [debounced, setDebounced] = useState(value)
      useEffect(() => {
        const timer = setTimeout(() => setDebounced(value), delay)
        return () => clearTimeout(timer)
      }, [value, delay])
      return debounced
    }`,
    'function B7() { const isMobile = useIsMobile(); const [open] = useState(isMobile) }',
    "function B8({ id }) { const q = useQuery({ queryKey: ['x', id], queryFn: () => id }); const [ready] = useState(q.isSuccess) }",
    'function B9({ value }) { const [text, setText] = useState(value); const [prev, setPrev] = useState(value); if (value !== prev) { setPrev(value); setText(value.trim()) } }',
  ].map((code) => [code, serverStateCase(code)]))('takes the probe %s', (_, read) => {
    expect(serverState(read)).toEqual([])
  })

  const advice = [
    serverStateCase("function Name({ id }) { const { data } = useRecord(id); const [name] = useState(data?.name ?? '') }"),
    serverStateCase('function useCopy(value) { const [copy] = useState(value) }'),
    serverStateCase('function Name({ name }) { const [n] = useState(name) }'),
  ]
  it('names the prop only for a prop, and gives the rename advice only for a prop', () => {
    expect(messagesOf(advice[0])[0]).not.toMatch(/prop|initialX/)
    expect(messagesOf(advice[1])[0]).not.toMatch(/prop/)
    expect(messagesOf(advice[2])[0]).toMatch(/name the prop initialX or defaultX/)
  })
})

describe('server-state/query-key-has-inputs', () => {
  it.each([
    ['reports a value that the query function reads and the key does not have', `function useRecords(search, page) {
      return useQuery({ queryKey: RecordKeys.list({ search }), queryFn: ({ signal }) => getRecordList({ query: { search, pageNumber: page }, signal }) })
    }`, ['page']],
    ['takes a key with each value, a key from a local const, client options, and module constants', `const PAGE_SIZE = 10
    function useRecord(id, params, suppressErrorToast) {
      const key = RecordKeys.list(params)
      const detail = queryOptions({ queryKey: RecordKeys.detail(id ?? ''), queryFn: ({ signal }) => getRecord({ path: { id: id! }, signal, options: { suppressErrorToast } }) })
      const list = useQuery({ queryKey: key, queryFn: async ({ signal }) => getRecordList({ query: { search: params.search, pageSize: PAGE_SIZE }, signal }) })
      return { detail, list }
    }`, []],
    ['takes a local that the query function reads when the key has each value it comes from', `function useRecords(params) {
      const search = params.search.trim()
      return useQuery({ queryKey: RecordKeys.list(params), queryFn: () => getRecordList({ query: { search, pageNumber: params.page } }) })
    }`, []],
    ['reports a local that a hook makes from a value in the key', `function useRecords(search) {
      const debounced = useDebouncedValue(search)
      return useQuery({ queryKey: RecordKeys.list({ search }), queryFn: () => getRecordList({ query: { search: debounced } }) })
    }`, ['debounced']],
    ['takes a useMemo or useCallback local that comes only from values in the key', `function useRecords(search, id) {
      const filter = useMemo(() => build(search), [search])
      const load = useCallback(() => getRecord(id), [id])
      return [
        useQuery({ queryKey: RecordKeys.list({ search }), queryFn: () => getRecordList({ query: { filter } }) }),
        useQuery({ queryKey: RecordKeys.detail(id), queryFn: () => load() }),
      ]
    }`, []],
    ['reports a local that refers to itself, and does not overflow the stack', `function useRecord(id) {
      const node = { self: () => node.id, id }
      return useQuery({ queryKey: RecordKeys.detail(id), queryFn: () => getRecord(node) })
    }`, ['node']],
    ['reports a field that the query function reads when the key has another field of the same value', `function useRecords(params) {
      return useQuery({ queryKey: RecordKeys.list(params.search), queryFn: () => getRecordList({ query: { search: params.search, pageNumber: params.page } }) })
    }`, ['params.page']],
    ['reports a whole value that the query function reads when the key has only a field of it', `function useRecords(params) {
      return useQuery({ queryKey: [...RecordKeys.lists(), { search: params.search }], queryFn: () => getRecordList({ query: { ...params } }) })
    }`, ['params']],
    ['takes a field of a value when the key has the whole value or the same field', `function useRecords(params, filter) {
      return [
        useQuery({ queryKey: RecordKeys.list(params), queryFn: () => getRecordList({ query: { search: params.search.trim(), pageNumber: params.page } }) }),
        useQuery({ queryKey: ['records', filter.text, filter.page], queryFn: () => getRecordList({ query: { search: filter.text, pageNumber: filter.page } }) }),
      ]
    }`, []],
  ].map(([title, code, names]) => [title, serverStateCase(code), names]))('%s', (_, read, names) => {
    expect(serverState(read)).toEqual(names.map((name) => `query-key-has-inputs: The query function reads \`${name}\``))
  })

  it.each([
    ['useQuery({ queryKey: ["x", id], queryFn: () => get({ id, page }) })', ['page'], 'id, page'],
    ['useQuery({ queryKey: ["x", params], queryFn: () => get({ ...params, tenantId }) })', ['tenantId'], 'params, tenantId'],
    ['queryOptions({ queryKey: ["x", id], queryFn: () => get({ id, date }) })', ['date'], 'id, date'],
    ['useQuery({ queryKey: ["x", params], queryFn: ({ signal }) => get({ page: params.page, search: params.search, size: PAGE_SIZE, signal }) })', [], 'params'],
    ['useQuery({ queryKey: ["x", params], queryFn: () => get({ filter: toFilter(params.search) }) })', [], 'params'],
    ['useQuery({ queryKey: ["x", id], queryFn: () => get({ id, options: { suppressErrorToast: opts.suppress } }) })', [], 'id, opts'],
    ['useQuery({ queryKey: ["x", search.trim()], queryFn: () => get({ search: search.trim() }) })', [], 'search'],
    ['(() => { const pageSize = 25; const now = Date.now(); return useQuery({ queryKey: ["x", params], queryFn: () => get({ ...params, pageSize, now }) }) })()', ['now'], 'params'],
    ['useInfiniteQuery({ queryKey: ["x", search], queryFn: ({ pageParam, signal }) => get({ search, pageParam, signal }), initialPageParam: 1, getNextPageParam: () => 2 })', [], 'search'],
  ].map(([call, names, parameters]) => [call, serverStateCase(`const PAGE_SIZE = 10\nfunction useProbe(${parameters}) { return ${call} }`), names]))('checks the probe %s', (_, read, names) => {
    expect(serverState(read)).toEqual(names.map((name) => `query-key-has-inputs: The query function reads \`${name}\``))
  })
})

describe('server-state/key-has-each-param', () => {
  const keysFile = 'src/domain/records/apis/record.keys.ts'
  const keysOf = (property) => `export const RecordKeys = {\n  all: ['records'] as const,\n  lists: () => [...RecordKeys.all, 'list'] as const,\n  ${property}\n}`
  const whole = (name) => `key-has-each-param: Put the parameter \`${name}\` in the key whole`
  const pattern = 'key-has-each-param: Take this parameter whole'

  it.each([
    ['a pattern in the signature that drops a field', 'list: ({ search }: RecordListParams) => [...RecordKeys.lists(), { search }] as const,', [pattern]],
    ['a pattern in the signature that keeps each field', 'list: ({ page, search }: RecordListParams) => [...RecordKeys.lists(), { page, search }] as const,', [pattern]],
    ['a pattern with an inline type', 'list: ({ search, page }: { search: string; page: number }) => [...RecordKeys.lists(), { search, page }] as const,', [pattern]],
    ['a pattern with a rest element', 'list: ({ search, ...rest }: RecordListParams) => [...RecordKeys.lists(), search, rest] as const,', [pattern]],
    ['a default value', 'list: (page = 1) => [...RecordKeys.lists(), page] as const,', [pattern]],
    ['a destructure in the body', 'list: (params: RecordListParams) => { const { search } = params; return [...RecordKeys.lists(), { search }] as const },', [whole('params')]],
    ['an alias in the body', 'list: (params: RecordListParams) => { const p = params; return [...RecordKeys.lists(), p.search] as const },', [whole('params')]],
    ['a field read', 'list: (params: RecordListParams) => [...RecordKeys.lists(), { search: params.search }] as const,', [whole('params')]],
    ['each field read', 'list: (params: RecordListParams) => [...RecordKeys.lists(), { page: params.page, search: params.search }] as const,', [whole('params')]],
    ['one field read twice', 'list: (params: RecordListParams) => [...RecordKeys.lists(), { search: params.search, page: params.search }] as const,', [whole('params')]],
    ['a method that reads a field', 'list(params: RecordListParams) { return [...RecordKeys.lists(), { search: params.search }] as const },', [whole('params')]],
    ['a spread into an object', 'list: (params: RecordListParams) => [...RecordKeys.lists(), { ...params }] as const,', [whole('params')]],
    ['a spread into the key', 'byIds: (ids: string[]) => [...RecordKeys.all, ...ids] as const,', [whole('ids')]],
    ['a parameter in an object', 'search: (search: string) => [...RecordKeys.lists(), { search }] as const,', [whole('search')]],
    ['a call on the parameter', 'day: (date: Date) => [...RecordKeys.all, date.toISOString()] as const,', [whole('date')]],
    ['the parameter given to a call', 'list: (params: RecordListParams) => listKey(params),', [whole('params')]],
    ['the whole parameter and a field of it', 'list: (params: RecordListParams) => [...RecordKeys.lists(), params, params.search] as const,', [whole('params')]],
    ['a parameter that the key does not use', 'detail: (id: string, tab: string) => [...RecordKeys.all, id] as const,', [whole('tab')]],
    ['a branch that returns a key with no parameter', 'list: (params: RecordListParams) => { if (flag) return [...RecordKeys.lists()] as const; return [...RecordKeys.lists(), params] as const },', [whole('params')]],
    ['a choice between two keys', 'list: (params: RecordListParams) => (flag ? [...RecordKeys.lists(), params] : [...RecordKeys.lists()]),', [whole('params')]],
  ].map(([title, property, expected]) => [title, serverStateCase(keysOf(property), keysFile), expected]))('reports %s', (_, read, expected) => {
    expect(serverState(read)).toEqual(expected)
  })

  const outside = serverStateCase(`${keysOf('list: (params: RecordListParams) => listKey(params),')}
export function listKey(params: RecordListParams) {
  return [...RecordKeys.lists(), { search: params.search }] as const
}`, keysFile)
  it('reports a function outside the object of the keys', () => {
    expect(serverState(outside)).toEqual([whole('params'), whole('params')])
  })

  const each = serverStateCase(keysOf(`list: (params: RecordListParams) => [...RecordKeys.lists(), params] as const,
  search: (search: string) => [...RecordKeys.lists(), 'search', search],
  page: (params: RecordListParams, page: number) => [...RecordKeys.lists(), params, page] satisfies readonly unknown[],
  details: () => [...RecordKeys.all, 'detail'] as const,
  detail(id: string) { return [...RecordKeys.all, 'detail', id] as const },
  named: function named(id: string) { return [...RecordKeys.all, id] as const },`), keysFile)
  it('takes each parameter whole as an item of the returned key', () => {
    expect(serverState(each)).toEqual([])
  })
})

describe('words made from a code', () => {
  it.each([
    "export const where = (zone) => zone.split('/').pop().replaceAll('_', ' ')",
    "export const where = (zone) => zone.replace(/_/g, ' ')",
    "export const where = (zone) => zone.replace('_', ' ')",
    "export const where = (zone) => zone.replaceAll('_', ` `)",
    "export const where = (zone) => zone.replace(/_+/g, ' ')",
    "export const where = (zone) => zone.split('_').join(' ')",
  ].map((code) => [code, addCase(code, 'src/zone.ts')]))('reports %s', (_, read) => {
    expect(messagesOf(read)).toEqual([codeToWordsRules[0].message])
  })

  it.each(['src/lib/zoned-time.ts', 'src/components/zone.tsx', 'src/examples/zone.tsx']
    .map((path) => [path, addCase("export const where = (zone: string) => zone.replaceAll('_', ' ')", path)]))('reports it in %s', (_, read) => {
    expect(messagesOf(read)).toEqual([codeToWordsRules[0].message])
  })

  const slug = addCase("export const slug = (text) => text.replaceAll(' ', '_')", 'src/zone.ts')
  it('accepts a replace that does not make words from a code', () => {
    expect(messagesOf(slug)).toEqual([])
  })
})

describe('the zone of the browser', () => {
  const code = 'export const hour = () => new Date().getHours()'
  it.each([
    ['src/components/clock.tsx', [browserZoneRules[2].message]],
    ['src/lib/zoned-time.ts', []],
    ['src/components/clock.test.tsx', []],
  ].map(([path, expected]) => [path, addCase(code, path), expected]))('reads a local hour in %s', (_, read, expected) => {
    expect(messagesOf(read)).toEqual(expected)
  })
})

describe('the way out of the app', () => {
  const wayOut = wayOutRules[0].message

  it.each([
    'window.location.reload()',
    'location.reload()',
    'window.location.assign(to)',
    'window.location.replace(to)',
    'document.location.replace(to)',
    'window.location.href = to',
    'location.href = to',
    'window.location = to',
    'location = to',
  ].map((statement) => {
    const code = `export const go = (to) => { ${statement}; return to }`
    const paths = ['src/components/page-error.tsx', 'src/domain/sessions/a.test.ts', 'src/routes/a.test.tsx', 'src/lib/zoned-time.ts']
    return [statement, paths.map((path) => [path, addCase(code, path)])]
  }))('reports %s in the app, in a test, and in the zone helpers', (_, reads) => {
    for (const [path, read] of reads)
      expect(messagesOf(read), path).toEqual([wayOut])
  })

  const pages = "export const pages = ['/auth/login?signedOut=1', `/auth/login?returnUrl=${encodeURIComponent('/')}`]"
  const inApp = addCase(pages, 'src/domain/auth/apis/auth-actions.ts')
  const inTest = addCase(pages, 'src/domain/auth/apis/auth-actions.test.ts')
  it('reports a login page in the app, and not in a test', () => {
    expect(messagesOf(inApp)).toEqual([loginPageRules[0].message, loginPageRules[0].message])
    expect(messagesOf(inTest)).toEqual([])
  })

  const leave = addCase("export const go = (to) => { window.location.replace('/auth/login?signedOut=1' + to); return new Date().getHours() }", 'src/domain/auth/apis/leave-the-app.ts')
  it('takes each way out in leave-the-app.ts, and still checks it for the other rules', () => {
    expect(messagesOf(leave)).toEqual([expect.stringContaining('zone of the browser')])
  })

  const read = addCase('export const here = () => window.location.pathname.replace(/\\/$/, "") + window.location.href', 'src/components/a.tsx')
  it('takes a read of the location and a replace on a string', () => {
    expect(messagesOf(read)).toEqual([])
  })
})
