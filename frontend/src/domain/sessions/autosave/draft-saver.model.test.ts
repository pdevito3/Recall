import { QueryClient } from '@tanstack/react-query'
import { afterEach, beforeEach, expect, test, vi } from 'vitest'

import type { JSONContent } from '@tiptap/react'

import { ProblemDetailsError } from '@/lib/problem-details'

import { parseDocument } from '../editor/content'

// A request of a tab reaches the server also while the tab is frozen. Only the answer waits until the tab wakes.
vi.mock('@/api/generated/sessions/clients/add-session', () => ({ addSession: (request: never) => viaTab(current, () => world.addSession(request)) }))
vi.mock('@/api/generated/sessions/clients/save-raw-draft', () => ({
  saveRawDraft: (request: { body: { text: string; baseRevision: number } }) => {
    const tab = current
    return viaTab(current, async () => {
      const reply = await world.saveRaw(request as never)
      world.noteSave?.(`tab ${tab?.name} saves "${tokensIn(request.body.text).join(' ')}" on base ${request.body.baseRevision} in ${(request as unknown as { path: { id: string } }).path.id}: ${reply.outcome}`)
      return reply
    })
  },
}))
vi.mock('@/api/generated/sessions/clients/save-cleaned-draft', () => ({ saveCleanedDraft: () => Promise.reject(new Error('The model has no cleaned channel.')) }))
vi.mock('@/api/generated/sessions/clients/get-session', () => ({
  getSession: (request: never) => {
    const tab = current
    return viaTab(current, async () => {
      const read = await world.getSession(request)
      world.noteSave?.(`tab ${tab?.name} reads ${read.id}: rev ${read.rawDraftRevision}`)
      return read
    })
  },
}))
// Every key of the model holds the text of its base, so a read of a base by number is a fault.
vi.mock('@/api/generated/sessions/clients/get-raw-revision', () => ({ getRawRevision: () => Promise.reject(new Error('The model keeps the base in each key.')) }))
vi.mock('@/api/generated/sessions/clients/get-cleaned-revision', () => ({ getCleanedRevision: () => Promise.reject(new Error('The model has no cleaned channel.')) }))
vi.mock('@/components/notification', () => ({ Notification: { error: () => undefined, success: () => undefined, info: () => undefined } }))
vi.mock('../apis/timeline-cache', () => ({ patchTimelineRow: () => undefined, refreshTimelineHead: () => Promise.resolve() }))

const defaultSeed = 20261107
const runs = Number(process.env.MODEL_RUNS ?? 40)
const stepsInARun = 120
const hasFullStorage = process.env.MODEL_NO_FULL_STORAGE === undefined
const hasSignOut = process.env.MODEL_NO_SIGN_OUT === undefined
const owner = 'person-a'

const randomFrom = (seed: number) => {
  let state = seed >>> 0
  const next = () => {
    state = (state + 0x6d2b79f5) >>> 0
    let mixed = Math.imul(state ^ (state >>> 15), 1 | state)
    mixed = (mixed + Math.imul(mixed ^ (mixed >>> 7), 61 | mixed)) ^ mixed
    return ((mixed ^ (mixed >>> 14)) >>> 0) / 4294967296
  }
  return { below: (limit: number) => Math.floor(next() * limit), chance: (odds: number) => next() < odds }
}

type Random = ReturnType<typeof randomFrom>

interface Row {
  id: string
  clientSessionId: string
  text: string
  draftRevision: number
  latestRevision: number
  lastChunkRevision: number
  savedAt: string | null
  revisions: { number: number; text: string; claimedAt: string; tookAt: number | null }[]
  chunks: string[]
}

const delay = (ms: number) => new Promise<void>((resolve) => setTimeout(resolve, ms))

/** The rules of `DraftSlot.Decide` on the Server, SES-36: a replay gets the answer of its first try, and only a base that is the current Draft wins. A request reaches the Server and answers later, also when its tab is gone. */
class ModelServer {
  rows: Row[] = []
  isDown = false
  losesNextReply = false
  refusesACreate = false
  latencyMs = () => 50
  readsTakeMs: number | null = null
  /** With MODEL_DEBUG, each save that a tab makes goes in the steps of the run. */
  noteSave: ((step: string) => void) | null = null

  private fault() {
    return new ProblemDetailsError({ type: 'about:blank', title: 'The server failed.', status: 503 })
  }

  private dto(row: Row) {
    return { id: row.id, clientSessionId: row.clientSessionId, rawText: row.text, cleanedText: null, rawDraftRevision: row.draftRevision, rawLatestRevision: row.latestRevision, cleanedDraftRevision: 0, cleanedLatestRevision: 0, cleanupStatus: 'NotRun', version: row.latestRevision, metadataVersion: 1 }
  }

  async addSession({ body }: { body: { clientSessionId: string; startedAt: string } }) {
    const latency = this.latencyMs()
    await delay(latency / 2)
    if (this.isDown) {
      await delay(latency / 2)
      throw this.fault()
    }
    if (this.refusesACreate) throw this.fault()
    let row = this.rows.find((other) => other.clientSessionId === body.clientSessionId)
    if (!row) {
      row = { id: `ses_${this.rows.length + 1}`, clientSessionId: body.clientSessionId, text: '', draftRevision: 0, latestRevision: 0, lastChunkRevision: 0, savedAt: null, revisions: [], chunks: [] }
      this.rows.push(row)
    }
    await delay(latency / 2)
    return this.dto(row)
  }

  async saveRaw({ path, body }: { path: { id: string }; body: { text: string; baseRevision: number; savedAt: string } }) {
    const latency = this.latencyMs()
    await delay(latency / 2)
    if (this.isDown) {
      await delay(latency / 2)
      throw this.fault()
    }
    const row = this.rows.find((other) => other.id === path.id)
    if (!row) {
      // A load of a tab resets the modules, so the error must come from the module of the newest load, or the saver does not know it.
      const current = await import('@/lib/problem-details')
      throw new current.ProblemDetailsError({ type: 'about:blank', title: 'Gone', status: 404 })
    }
    const reply = this.decide(row, body)
    await delay(latency / 2)
    if (this.losesNextReply) {
      this.losesNextReply = false
      throw this.fault()
    }
    return reply
  }

  private decide(row: Row, save: { text: string; baseRevision: number; savedAt: string }) {
    const answer = (outcome: string, draftRevision: number | null) => ({ outcome, draftRevision, latestRevision: row.latestRevision, savedAt: row.savedAt ?? save.savedAt, cleanupStatus: 'NotRun', version: row.latestRevision })
    const earlier = row.revisions.find((old) => old.claimedAt === save.savedAt && old.text === save.text)
    if (earlier) return earlier.tookAt === null ? answer('Lost', null) : answer('Kept', earlier.tookAt)
    if (save.text === row.text) return answer('Unchanged', row.draftRevision)
    const number = row.latestRevision + 1
    const wins = save.baseRevision === row.draftRevision
    row.revisions.push({ number, text: save.text, claimedAt: save.savedAt, tookAt: wins ? number : null })
    row.latestRevision = number
    if (!wins) return answer('Lost', null)
    row.text = save.text
    row.draftRevision = number
    row.savedAt = save.savedAt
    return answer('Won', number)
  }

  async getSession({ path }: { path: { id: string } }) {
    const latency = this.readsTakeMs ?? this.latencyMs()
    await delay(latency)
    const row = this.rows.find((other) => other.id === path.id)
    if (!row) throw new ProblemDetailsError({ type: 'about:blank', title: 'Gone', status: 404 })
    return this.dto(row)
  }

  appendChunk(row: Row, chunk: string) {
    const number = row.latestRevision + 1
    row.text = withChunk(row.text, chunk)
    row.savedAt = new Date().toISOString()
    row.revisions.push({ number, text: row.text, claimedAt: row.savedAt, tookAt: number })
    row.draftRevision = number
    row.latestRevision = number
    row.lastChunkRevision = number
    row.chunks.push(chunk)
  }

  allText() {
    return this.rows.flatMap((row) => [row.text, ...row.revisions.map(({ text }) => text)]).join(' ')
  }
}

let world = new ModelServer()

function storageWith(isFull: () => boolean) {
  const values = new Map<string, string>()
  // A full storage has room only for what a page removed since it became full, as a browser quota has.
  let room = 0
  const api = {
    getItem: (key: string) => values.get(key) ?? null,
    setItem: (key: string, value: string) => {
      if (!isFull()) room = 0
      else if (value.length > room) throw new DOMException('full', 'QuotaExceededError')
      else room -= value.length
      values.set(key, String(value))
    },
    removeItem: (key: string) => {
      if (isFull()) room += values.get(key)?.length ?? 0
      values.delete(key)
    },
    clear: () => values.clear(),
    key: (index: number) => [...values.keys()][index] ?? null,
    get length() {
      return values.size
    },
  }
  return new Proxy(api, {
    ownKeys: () => [...values.keys()],
    getOwnPropertyDescriptor: (_, key) => (typeof key === 'string' && values.has(key) ? { enumerable: true, configurable: true, value: values.get(key) } : undefined),
  })
}

/**
 * The tab whose code runs now. A timer, a request, a lock event, or a message runs as the tab that it belongs to, and
 * the code that it starts goes on as that tab. A fake timer runs only after each promise of the timer before it, so
 * the promises of one timer run as its tab.
 */
let current: Tab | null = null

function becomeCurrent(tab: Tab | null) {
  current = tab
}

interface Waiting {
  timer: unknown
  work: () => void
}

/** The fake timers, before the stub that runs each timer as its tab. */
const clock = { set: setTimeout, clear: clearTimeout }

function runWaiting(tab: Tab, one: Waiting) {
  const at = tab.waiting.indexOf(one)
  if (at < 0 || tab.isFrozen || tab.isClosed) return
  tab.waiting.splice(at, 1)
  current = tab
  one.work()
}

/** Runs work as `tab`: never for a closed tab, and after the wake for a frozen tab. */
function asTab(tab: Tab | null, work: () => void, timer: unknown = null) {
  if (tab?.isClosed) return
  if (tab?.isFrozen) {
    tab.waiting.push({ timer, work })
    return
  }
  current = tab
  work()
}

function tabTimeout(work: (...args: unknown[]) => void, ms?: number, ...args: unknown[]) {
  const tab = current
  const timer: unknown = clock.set(() => asTab(tab, () => work(...args), timer), ms)
  return timer
}

function tabClearTimeout(timer: unknown) {
  clock.clear(timer as never)
  if (timer === null || timer === undefined) return
  for (const tab of everyTab) {
    const at = tab.waiting.findIndex((one) => one.timer === timer)
    if (at >= 0) tab.waiting.splice(at, 1)
  }
}

const everyTab = new Set<Tab>()

/** The answer of a request goes to the tab that asked, when that tab runs. The server itself is no tab. */
function viaTab<T>(tab: Tab | null, request: () => Promise<T>): Promise<T> {
  const before = current
  current = null
  const answer = request()
  current = before
  return new Promise<T>((resolve, reject) => {
    answer.then(
      (value) => clock.set(() => asTab(tab, () => resolve(value)), 0),
      (error: unknown) => clock.set(() => asTab(tab, () => reject(error)), 0),
    )
  })
}

/** The Web Locks of the browser: one holder of a name, a line of the tabs that wait, a steal, and the end of each lock of a tab that closed or crashed. */
class ModelLocks {
  private readonly held = new Map<string, LockAsk>()
  private line: LockAsk[] = []

  managerFor(tab: Tab) {
    return { request: (name: string, options: LockOptions, callback: (lock: Lock | null) => Promise<unknown>) => this.request(tab, name, options, callback) }
  }

  holderOf(name: string) {
    return this.held.get(name)?.tab ?? null
  }

  private request(tab: Tab, name: string, options: LockOptions, callback: (lock: Lock | null) => Promise<unknown>) {
    return new Promise<unknown>((resolve, reject) => {
      const ask: LockAsk = { tab, name, callback, resolve, reject }
      if (options.steal) {
        const holder = this.held.get(name)
        this.held.delete(name)
        if (holder) clock.set(() => asTab(holder.tab, () => holder.reject(new DOMException('stolen', 'AbortError'))), 0)
        this.grant(ask)
      } else if (options.ifAvailable) {
        if (!this.held.has(name) && !this.line.some((other) => other.name === name)) this.grant(ask)
        else this.answer(ask, null)
      } else {
        const signal = options.signal
        signal?.addEventListener('abort', () => {
          if (!this.line.includes(ask)) return
          this.line = this.line.filter((other) => other !== ask)
          clock.set(() => asTab(tab, () => reject(new DOMException('aborted', 'AbortError'))), 0)
        })
        if (signal?.aborted) return
        this.line.push(ask)
        this.next(name)
      }
    })
  }

  private grant(ask: LockAsk) {
    this.held.set(ask.name, ask)
    this.answer(ask, { name: ask.name, mode: 'exclusive' } as Lock, () => {
      if (this.held.get(ask.name) !== ask) return
      this.held.delete(ask.name)
      this.next(ask.name)
    })
  }

  private answer(ask: LockAsk, lock: Lock | null, release = () => undefined) {
    clock.set(() => asTab(ask.tab, () => {
      ask.callback(lock).then(
        (value) => {
          release()
          ask.resolve(value)
        },
        (error: unknown) => {
          release()
          ask.reject(error)
        },
      )
    }), 0)
  }

  private next(name: string) {
    if (this.held.has(name)) return
    const ask = this.line.find((other) => other.name === name)
    if (!ask) return
    this.line = this.line.filter((other) => other !== ask)
    this.grant(ask)
  }

  /** A tab that closed or crashed holds no lock and waits for none. */
  closeTabOf(tab: Tab) {
    this.line = this.line.filter((ask) => ask.tab !== tab)
    for (const [name, ask] of [...this.held]) {
      if (ask.tab !== tab) continue
      this.held.delete(name)
      this.next(name)
    }
  }
}

interface LockAsk {
  tab: Tab
  name: string
  callback: (lock: Lock | null) => Promise<unknown>
  resolve: (value: unknown) => void
  reject: (error: unknown) => void
}

let locks = new ModelLocks()
let tabNames = 0

/** A channel belongs to the tab that runs when the channel opens, so that a closed tab hears nothing. A message waits while its tab is frozen. */
class ModelChannels {
  private open = new Set<ModelChannel>()
  /** The messages on their way, so a word that a hand-over carries is not a lost word. */
  readonly onTheWay = new Set<{ to: ModelChannel; data: unknown }>()

  add(channel: ModelChannel) {
    this.open.add(channel)
  }

  remove(channel: ModelChannel) {
    this.open.delete(channel)
  }

  /** The answer is the words of the messages that will not come to the tab now. */
  closeChannelsOf(tab: object) {
    const lost: unknown[] = []
    for (const message of [...this.onTheWay]) {
      if (message.to.tab !== tab) continue
      this.onTheWay.delete(message)
      lost.push(message.data)
    }
    for (const channel of [...this.open]) if (channel.tab === tab) this.open.delete(channel)
    return JSON.stringify(lost)
  }

  post(from: ModelChannel, data: unknown) {
    for (const channel of [...this.open]) {
      if (channel === from || channel.name !== from.name) continue
      const message = { to: channel, data }
      this.onTheWay.add(message)
      clock.set(() => asTab(channel.tab, () => {
        this.onTheWay.delete(message)
        if (this.open.has(channel)) channel.onmessage?.({ data } as MessageEvent)
      }), 1)
    }
  }
}

let channels = new ModelChannels()

class ModelChannel {
  readonly name: string
  readonly tab: Tab | null
  onmessage: ((event: MessageEvent) => void) | null = null

  constructor(name: string) {
    this.name = name
    this.tab = current
    channels.add(this)
  }

  postMessage(data: unknown) {
    channels.post(this, data)
  }

  close() {
    channels.remove(this)
  }

  addEventListener(_: 'message', listener: (event: MessageEvent) => void) {
    this.onmessage = listener
  }

  removeEventListener() {
    this.onmessage = null
  }
}

type DraftSaverModule = typeof import('./draft-saver')
type TabKeysModule = typeof import('./tab-keys')
type Saver = ReturnType<DraftSaverModule['draftSaverFor']>
type Notice = ReturnType<DraftSaverModule['draftNotices']>[number]

interface Page {
  saver: Saver
  stopListening: () => void
  clientSessionId: string
  /** The words in the editor, or null while the page reads the entry or shows the words of another tab. */
  editor: string | null
  held: string | null
  /** Where the entry is edited, as the page last rendered it. */
  place: 'waiting' | 'here' | 'elsewhere'
  /** The page showed the words of another tab since it last edited, so a take opens the text that the read of the take gave. */
  wasElsewhere: boolean
}

// A line break in a document is the escape `\n`, so a token can follow the letter `n`. A person types `w`, a recording adds `c`, and another device types `d`.
const tokensIn = (text: string): string[] => text.match(/(?<=^|[^\w]|\\n)[wcd]\d+x\b/g) ?? []

/** The editor writes a document, so a word typed after a merge goes at the end of the last paragraph, and the text stays a document. */
function typedInto(text: string, token: string): string {
  const document = parseDocument(text)
  if (!document) return text ? `${text} ${token}` : token
  const blocks = document.content ?? []
  const last = blocks[blocks.length - 1] as JSONContent | undefined
  if (last?.type !== 'paragraph') return JSON.stringify({ ...document, content: [...blocks, { type: 'paragraph', content: [{ type: 'text', text: token }] }] })
  const content = last.content ?? []
  const end = content[content.length - 1] as JSONContent | undefined
  const typed = end?.type === 'text' && !end.marks ? [...content.slice(0, -1), { ...end, text: `${end.text ?? ''} ${token}` }] : [...content, { type: 'text', text: content.length > 0 ? ` ${token}` : token }]
  return JSON.stringify({ ...document, content: [...blocks.slice(0, -1), { ...last, content: typed }] })
}

const textOf = (token: string): JSONContent => ({ type: 'text', text: token })
const listItemOf = (token: string): JSONContent => ({ type: 'listItem', content: [{ type: 'paragraph', content: [textOf(token)] }] })

/** A word at the end of a paragraph or a heading, or in a list item, old or new. */
function withWordAtEnd(block: JSONContent, token: string, random: Random): JSONContent {
  if (block.type === 'bulletList') {
    const items = block.content ?? []
    const at = random.below(items.length + 1)
    if (at === items.length) return { ...block, content: [...items, listItemOf(token)] }
    const paragraphs = items[at].content ?? []
    const last = paragraphs[paragraphs.length - 1] ?? { type: 'paragraph' }
    return { ...block, content: items.map((item, index) => (index === at ? { ...item, content: [...paragraphs.slice(0, -1), withWordAtEnd(last, token, random)] } : item)) }
  }
  const content = block.content ?? []
  const end = content[content.length - 1] as JSONContent | undefined
  return { ...block, content: end?.type === 'text' && !end.marks ? [...content.slice(0, -1), { ...end, text: `${end.text ?? ''} ${token}` }] : [...content, textOf(content.length > 0 ? ` ${token}` : token)] }
}

/** Half of the words go at the end, as `typedInto` puts them. The others go into a heading, a paragraph, or a list, or start a new heading or list, so that a merge meets structure and changes side by side. */
function typedSomewhere(text: string, token: string, random: Random): string {
  const roll = random.below(100)
  if (roll < 50) return typedInto(text, token)
  const document = parseDocument(text) ?? { type: 'doc', content: text.trim() ? [{ type: 'paragraph', content: [textOf(text)] }] : [] }
  const blocks = [...(document.content ?? [])]
  const at = random.below(blocks.length + 1)
  if (roll < 75 && blocks.length > 0) blocks[Math.min(at, blocks.length - 1)] = withWordAtEnd(blocks[Math.min(at, blocks.length - 1)], token, random)
  else if (roll < 88) blocks.splice(at, 0, { type: 'heading', attrs: { level: 2 }, content: [textOf(token)] })
  else blocks.splice(at, 0, { type: 'bulletList', content: [listItemOf(token)] })
  return JSON.stringify({ ...document, content: blocks })
}

/** The text with one word gone, as a person deletes it. */
function withoutWord(text: string, token: string): string {
  const cut = (words: string) => words.replace(new RegExp(`(^|\\s+)${token}(?=\\s|$)`), '')
  const document = parseDocument(text)
  if (!document) return cut(text)
  const strip = (node: JSONContent): JSONContent =>
    node.type === 'text' ? { ...node, text: cut(node.text ?? '') } : node.content ? { ...node, content: node.content.map(strip).filter((child) => child.type !== 'text' || child.text !== '') } : node
  return JSON.stringify(strip(document))
}

/** A chunk of a recording joins as `RawTranscript.Append` on the Server joins it: a new paragraph of a document, or after an empty line of plain text. */
function withChunk(text: string, chunk: string): string {
  const paragraph = { type: 'paragraph', content: [{ type: 'text', text: chunk }] }
  if (!text.trim()) return JSON.stringify({ type: 'doc', content: [paragraph] })
  const document = parseDocument(text)
  if (!document) return `${text}\n\n${chunk}`
  const blocks = document.content ?? []
  const isEmpty = blocks.length === 1 && blocks[0].type === 'paragraph' && (blocks[0].content ?? []).length === 0
  return JSON.stringify({ ...document, content: [...(isEmpty ? [] : blocks), paragraph] })
}

class Tab {
  mod!: DraftSaverModule
  keys!: TabKeysModule
  queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  page: Page | null = null
  isClosed = false
  tokenCount = 0

  /** The name of the tab in the steps of a run. */
  readonly name = (tabNames += 1)
  isFrozen = false
  /** The timers, answers, and messages that came while the tab was frozen, in order. */
  readonly waiting: Waiting[] = []

  async load() {
    everyTab.add(this)
    becomeCurrent(this)
    // The lock reads the lock manager of its page when its module loads.
    vi.stubGlobal('navigator', { locks: locks.managerFor(this) })
    vi.resetModules()
    this.mod = await import('./draft-saver')
    this.keys = await import('./tab-keys')
    becomeCurrent(this)
    this.keys.joinTheOtherTabs(async () => {
      await Promise.race([this.mod.settleDraftSavers(), delay(4000)])
      return { wordsOnlyInMemory: this.mod.wordsOfThisTabOnlyInMemory(), hasUnsavedTags: false }
    })
    this.keys.watchOtherTabs(() => this.resume())
  }

  /** The browser stops the code of the tab: no timer, answer, message, or lock event reaches it until it wakes. */
  freeze() {
    this.isFrozen = true
  }

  wake() {
    this.isFrozen = false
    for (const one of this.waiting) clock.set(() => runWaiting(this, one), 0)
  }

  /** The effect of the root route: a load, a tab that left, a tab that the person sees again, or a network that returns. */
  resume() {
    if (!this.isClosed) void this.run(() => this.mod.resumeUnsentDrafts(owner, this.queryClient))
  }

  /** Runs code of this tab, so that a timer, a request, or a channel that it makes is of this tab. The code that it starts goes on as this tab. */
  run<T>(work: () => T): T {
    becomeCurrent(this)
    return work()
  }

  /** Reads the state of this tab, as a person who looks at it, also when it is frozen. */
  look<T>(work: () => T): T {
    const before = current
    becomeCurrent(this)
    try {
      return work()
    } finally {
      current = before
    }
  }

  /** The entry page or the composer. It edits only while this tab holds the lock of the entry, and else it shows the words of the tab that edits. */
  openDraft(clientSessionId: string, sessionId: string | null, baseRevision: number, isNewEntry = false) {
    const target = { owner, channel: 'raw' as const, clientSessionId, startedAt: '2026-11-07T00:00:00.000Z', sessionId, baseRevision, baseText: '' }
    this.run(() => {
      const saver = this.mod.draftSaverFor(target, this.queryClient, isNewEntry)
      const page: Page = { saver, stopListening: () => undefined, clientSessionId, editor: null, held: null, place: 'waiting', wasElsewhere: false }
      this.page = page
      // A page opens its editor after the render, so a take of the words ends first.
      page.stopListening = saver.subscribe(() => queueMicrotask(() => this.notePlace(page)))
      saver.subscribeToNewWords((words) => {
        if (page.place === 'here') page.editor = words
      })
      this.notePlace(page)
    })
    this.resume()
  }

  /** The page edits from the moment that this tab holds the lock, and it opens the words of the saver or of the Draft. A page that lost the lock shows the words of the other tab, and it holds none. */
  private notePlace(page: Page) {
    if (this.isClosed || this.page !== page) return
    const place = page.saver.getSnapshot().editing
    if (place === page.place) return
    page.place = place
    if (place !== 'here') {
      if (place === 'elsewhere') page.wasElsewhere = true
      page.editor = null
      page.held = null
      return
    }
    const kept = page.saver.wordsToOpen()
    const id = page.saver.getSnapshot().sessionId
    page.held = page.saver.wordsInHold()
    // As the entry page: after a take with no words to open, the editor opens the text of the base, not an older read of the page.
    const ofTheTake = page.wasElsewhere && page.held === null ? page.saver.wordsOfTheBase() : null
    page.wasElsewhere = false
    page.editor = kept ?? ofTheTake ?? (id ? null : '')
    if (page.editor === null) {
      page.saver.takeHold()
      void this.readInto(page, id as string)
    }
  }

  private async readInto(page: Page, id: string) {
    for (;;) {
      const session = await viaTab(this, () => world.getSession({ path: { id } })).catch((error: unknown) => (error instanceof ProblemDetailsError && error.status === 404 ? ('gone' as const) : null))
      if (this.isClosed || this.page !== page || page.place !== 'here' || page.editor !== null) return

      if (session === null) {
        await delay(2000)
        continue
      }
      this.run(() => {
        if (session === 'gone') {
          page.editor = page.saver.joinHoldInto('')
          return
        }
        page.saver.moveBaseTo(session.rawDraftRevision, session.rawText)
        page.editor = page.saver.joinHoldInto(session.rawText)
      })
      return
    }
  }

  /** False when the page shows the words of another tab, so the person cannot type. */
  type(token: string, random?: Random) {
    const page = this.page
    if (!page || page.place !== 'here') return false
    this.run(() => {
      if (page.editor === null) {
        page.held = typedInto(page.held ?? '', token)
        page.saver.holdInOutbox(page.held)
        return
      }
      page.editor = random ? typedSomewhere(page.editor, token, random) : typedInto(page.editor, token)
      page.saver.edit(page.editor)
    })
    return true
  }

  /** The words that a page with no edit shows. A storage event makes the page read them again. */
  wordsOfTheEditor() {
    return this.look(() => {
      this.mod.refreshWordsOfEditors()
      return this.page?.saver.getSnapshot().wordsOfTheEditor ?? null
    })
  }

  /** Edit here, on a page that shows the words of another tab. */
  editHere() {
    const page = this.page
    if (!page || page.place !== 'elsewhere') return false
    this.run(() => page.saver.takeHere())
    return true
  }

  /** The person deletes one word that the person typed. The answer is the word, or null when the screen shows none. */
  deleteAWord(random: Random): string | null {
    const page = this.page
    const own = tokensIn(page?.editor ?? '').filter((token) => token.startsWith('w'))
    if (!page || page.place !== 'here' || page.editor === null || own.length === 0) return null
    const token = own[random.below(own.length)]
    this.run(() => {
      page.editor = withoutWord(page.editor as string, token)
      page.saver.edit(page.editor)
    })
    return token
  }

  /** Done, or a move away from a page that shows the words of another tab. */
  done() {
    const page = this.page
    if (!page || (page.place === 'here' && (page.editor === null || page.saver.hasConflict()))) return
    this.run(() => {
      page.saver.flush()
      page.stopListening()
    })
    this.page = null
  }

  /** Show the newest words replaces the words of the notice on the page, so the person knows that those words leave the Draft. */
  async answerConflict(random: Random, trace: Trace) {
    const page = this.page
    if (!page || page.place !== 'here' || page.editor === null || page.saver.getSnapshot().conflict === null) return
    if (random.chance(0.5)) {
      const before = tokensIn(page.editor)
      const newest = await this.run(() => page.saver.takeNewestWords())
      if (newest !== null && this.page === page) {
        const after = tokensIn(newest)
        for (const token of before) if (!after.includes(token)) trace.knownToBeGone.add(token)
        page.editor = newest
      }
      return
    }
    const kept: unknown = await this.run(() => page.saver.keepWords(page.editor as string))
    if (typeof kept === 'string' && this.page === page) page.editor = kept
  }

  /** The effect of an open entry page: a newer Draft replaces the words on the screen when nothing is unsaved. */
  async refetch() {
    const page = this.page
    const id = page?.saver.getSnapshot().sessionId
    if (!page || !id || page.place !== 'here' || page.editor === null) return
    const session = await viaTab(this, () => world.getSession({ path: { id } })).catch(() => null)
    if (!session || this.isClosed || this.page !== page || page.place !== 'here') return
    this.run(() => {
      const { baseRevision, conflict } = page.saver.getSnapshot()
      if (session.rawDraftRevision <= baseRevision || page.saver.unsettledText() !== null || conflict !== null) return
      page.saver.moveBaseTo(session.rawDraftRevision, session.rawText)
      page.editor = session.rawText
    })
  }

  notices(): Notice[] {
    return this.look(() => this.mod.draftNotices())
  }

  /** A close that a person makes: the page hears `pagehide`, and its timers and listeners end. The browser frees its locks. */
  close() {
    this.run(() => {
      this.keys.noteThisTabLeaves()
      this.mod.stopDraftSavers()
    })
    this.isClosed = true
    channels.closeChannelsOf(this)
    locks.closeTabOf(this)
  }

  /** A crash: no `pagehide`, and no code of this tab writes to storage again. The browser frees its locks. */
  kill(storage: Storage) {
    const before = Object.keys(storage).map((key) => [key, storage.getItem(key) as string] as const)
    this.look(() => this.mod.stopDraftSavers())
    for (const key of Object.keys(storage)) storage.removeItem(key)
    for (const [key, value] of before) storage.setItem(key, value)
    this.isClosed = true
    channels.closeChannelsOf(this)
    locks.closeTabOf(this)
  }
}

interface Trace {
  steps: string[]
  discarded: Set<string>
  typed: Set<string>
  /** The newest word that each tab typed into each entry. */
  lastTyped: Map<string, { token: string; clientSessionId: string }>
  /** A notice named these words, and the person answered it. */
  knownToBeGone: Set<string>
  /** The words that a person deleted, with the entry, and where the word is: not yet in the Draft, as a save in flight can still bring it, in the Draft, or out of it since. */
  deleted: Map<string, { clientSessionId: string; place: 'notYet' | 'inDraft' | 'left' }>
}

/**
 * The Draft invariant: at rest, each word that a person typed last into an entry is in the Draft of that entry, or a notice or a conflict on a page names it.
 * A word in a Revision only is not enough, because the person looks at the Draft. A notice of History excuses only words that the Draft took once and that left it later:
 * the words of a `Lost` save go into the Draft by a merge, with no click, once the other writer stops.
 */
function lastWordsThatLeftTheDraftWithNoNotice(tabs: Tab[], storage: Storage, trace: Trace) {
  const notices = tabs.flatMap((tab) => tab.notices())
  const namedInHistory = new Set(tokensIn(notices.map((notice) => (notice.kind === 'inHistory' ? notice.words : '')).join(' ')))
  const named = new Set(tokensIn(notices.map((notice) => (notice.kind !== 'inHistory' && 'words' in notice ? notice.words : '')).join(' ')))
  for (const tab of tabs) {
    const { conflict, status, isOnlyInMemory } = tab.page?.saver.getSnapshot() ?? { conflict: null, status: { kind: 'idle' }, isOnlyInMemory: false }
    const onScreen = tokensIn(tab.page?.editor ?? '')
    if (status.kind === 'refused' || isOnlyInMemory || conflict === 'discardedInAnotherTab') for (const token of onScreen) named.add(token)
    else if (conflict === 'wordsMovedToHistory') for (const token of onScreen) namedInHistory.add(token)
  }
  // A key that holds words only as a notice of History is no queue: no save goes from it.
  const waiting = (value: string | null) => {
    const entry = (() => {
      try {
        return JSON.parse(value ?? 'null') as unknown
      } catch {
        return null
      }
    })()
    if (entry === null || typeof entry !== 'object' || !('change' in entry)) return value ?? ''
    const { change, held } = entry as { change: { value: string } | null; held?: string | null }
    return `${change?.value ?? ''} ${held ?? ''}`
  }
  const keys = new Set(tokensIn(Object.keys(storage).map((key) => waiting(storage.getItem(key))).join(' ')))
  return [...trace.lastTyped.values()].filter(({ token, clientSessionId }) => {
    const row = world.rows.find((other) => other.clientSessionId === clientSessionId)
    const tookOnce = tokensIn((row?.revisions ?? []).filter(({ tookAt }) => tookAt !== null).map(({ text }) => text).join(' '))
    const isExcused = named.has(token) || (namedInHistory.has(token) && tookOnce.includes(token))
    return !tokensIn(row?.text ?? '').includes(token) && !isExcused && !keys.has(token) && !trace.discarded.has(token) && !trace.knownToBeGone.has(token)
  }).map(({ token, clientSessionId }) => `${token} in ${clientSessionId}`)
}

function wordsThatVanished(tabs: Tab[], storage: Storage, trace: Trace) {
  const keys = `${Object.keys(storage).map((key) => storage.getItem(key)).join(' ')} ${[...channels.onTheWay].map(({ data }) => JSON.stringify(data)).join(' ')}`
  const notices = tabs.flatMap((tab) => tab.notices()).map((notice) => ('words' in notice ? notice.words : '')).join(' ')
  const screens = tabs.map((tab) => tab.page?.editor ?? tab.page?.held ?? '').join(' ')
  const present = new Set(tokensIn(`${world.allText()} ${keys} ${notices} ${screens}`))
  return [...trace.typed].filter((token) => !present.has(token) && !trace.discarded.has(token))
}

const chunksPushedOut = () => world.rows.flatMap((row) => row.chunks.filter((chunk) => !row.text.includes(chunk)).map((chunk) => `${chunk} in ${row.id}`))

class ModelRun {
  private readonly random: Random
  private readonly tabs: Tab[] = []
  private readonly trace: Trace = { steps: [], discarded: new Set(), typed: new Set(), lastTyped: new Map(), knownToBeGone: new Set(), deleted: new Map() }
  private storageIsFull = false
  private readonly storage = storageWith(() => this.storageIsFull)
  private readonly pointers = new Map<Tab, string>()
  private tokenCount = 0
  /** The tab that the person used last. A move to another tab takes the person a moment. */
  private lastTab: Tab | null = null

  constructor(seed: number) {
    this.random = randomFrom(seed)
    // The wait after losses has a random part. A second stream from the seed keeps each run the same each time.
    const jitter = randomFrom(seed ^ 0x9e3779b9)
    vi.spyOn(Math, 'random').mockImplementation(() => jitter.below(1_000_000) / 1_000_000)
    vi.stubGlobal('localStorage', this.storage)
    world = new ModelServer()
    world.latencyMs = () => 20 + this.random.below(1500)
    if (process.env.MODEL_DEBUG) world.noteSave = (step) => this.note(step)
    channels = new ModelChannels()
    locks = new ModelLocks()
    everyTab.clear()
    tabNames = 0
  }

  private readonly tabNames = new Map<Tab, number>()

  private nameTab(tab: Tab) {
    const name = this.tabNames.size
    this.tabNames.set(tab, name)
    return name
  }

  private note(step: string) {
    this.trace.steps.push(step)
  }

  private pick<T>(items: T[]): T | undefined {
    return items[this.random.below(items.length)]
  }

  private newEntryId() {
    return `client-${this.random.below(1e9)}`
  }

  private async openTab(pointer?: string) {
    const clientSessionId = pointer ?? this.newEntryId()
    const tab = new Tab()
    await tab.load()
    this.tabs.push(tab)
    const row = world.rows.find((other) => other.clientSessionId === clientSessionId)
    tab.openDraft(clientSessionId, row?.id ?? null, row?.draftRevision ?? 0, pointer === undefined)
    this.pointers.set(tab, clientSessionId)
    this.note(`open tab ${tab.name} on ${clientSessionId}`)
    return tab
  }

  private removeTab(tab: Tab) {
    this.tabs.splice(this.tabs.indexOf(tab), 1)
  }

  private async step() {
    const roll = this.random.below(100)
    const tab = this.pick(this.tabs)
    if (!tab) {
      await this.openTab()
      return
    }
    const index = tab.name
    const isAPersonOn = roll < 40 || (roll >= 62 && roll < 84) || roll >= 87
    if (isAPersonOn && tab !== this.lastTab) {
      await vi.advanceTimersByTimeAsync(100)
      this.note(`the person goes to tab ${index}`)
    }
    if (isAPersonOn) this.lastTab = tab
    if (tab.isFrozen && isAPersonOn && !(roll >= 68 && roll < 81)) {
      this.note(`tab ${index} is frozen, so the person waits`)
      return
    }
    if (roll < 40) {
      const deleted = this.random.chance(0.1) ? tab.deleteAWord(this.random) : null
      if (deleted !== null && tab.page) {
        this.trace.discarded.add(deleted)
        // A copy that another tab, a notice, or a key still holds has no base, so its merge keeps each word that it holds, as the merge of the iOS client does.
        const others = this.tabs.filter((other) => other !== tab)
        const copies = `${allKeys(this.storage)} ${others.map((other) => other.page?.editor ?? other.page?.held ?? '').join(' ')} ${this.tabs.flatMap((other) => other.notices()).map((notice) => ('words' in notice ? notice.words : '')).join(' ')}`
        if (!tokensIn(copies).includes(deleted)) this.trace.deleted.set(deleted, { clientSessionId: tab.page.clientSessionId, place: tokensIn(world.rows.find((row) => row.clientSessionId === tab.page?.clientSessionId)?.text ?? '').includes(deleted) ? 'inDraft' : 'notYet' })
        this.note(`tab ${index} deletes ${deleted}`)
        return
      }
      if (!tab.page) tab.openDraft(this.newEntryId(), null, 0, true)
      if (tab.page?.place !== 'here') {
        if (this.random.chance(0.5) && tab.editHere()) this.note(`tab ${index} presses Edit here`)
        else this.note(`tab ${index} shows the words of another tab, so the person does not type there`)
        return
      }
      const token = `w${(this.tokenCount += 1)}x`
      this.trace.typed.add(token)
      tab.type(token, this.random)
      const entry = tab.page?.clientSessionId as string
      this.trace.lastTyped.set(`${this.tabNames.get(tab) ?? this.nameTab(tab)} ${entry}`, { token, clientSessionId: entry })
      this.note(`tab ${index} types ${token}`)
    } else if (roll < 44) {
      const row = this.pick(world.rows)
      if (row) void this.anotherDeviceTypesInto(row)
    } else if (roll < 62) {
      if (this.random.chance(0.12)) {
        await this.freezeOrWake(tab)
        return
      }
      const ms = this.random.below(2500)
      await vi.advanceTimersByTimeAsync(ms)
      this.note(`${ms} ms pass`)
    } else if (roll < 68) {
      if (this.tabs.length < 3) {
        const same = this.random.chance(0.5) ? tab.page?.clientSessionId : undefined
        await this.openTab(same)
      }
    } else if (roll < 73) {
      if (tab.isFrozen) {
        // The browser ends a frozen page with no `pagehide`, as at a crash.
        this.crash(tab)
        this.note(`tab ${index} is frozen, and the person closes it`)
      } else if (tab.look(() => this.mod(tab).isAnyWordOnlyInMemory())) {
        this.note(`tab ${index} would close, and the browser warns, so the person stays`)
      } else {
        tab.close()
        this.removeTab(tab)
        this.note(`tab ${index} closes`)
      }
    } else if (roll < 76) {
      this.crash(tab)
      this.note(`tab ${index} crashes`)
    } else if (roll < 81) {
      const pointer = tab.page?.clientSessionId
      if (tab.isFrozen) {
        this.crash(tab)
        this.note(`tab ${index} is frozen, and the person reloads it`)
        await this.openTab(pointer)
      } else if (tab.look(() => this.mod(tab).isAnyWordOnlyInMemory())) {
        this.note(`tab ${index} would reload, and the browser warns, so the person stays`)
      } else {
        tab.close()
        this.removeTab(tab)
        this.note(`tab ${index} reloads`)
        await this.openTab(pointer)
      }
    } else if (roll < 85) {
      world.isDown = !world.isDown
      this.note(world.isDown ? 'the server goes down' : 'the server comes back')
    } else if (roll < 87) {
      this.storageIsFull = hasFullStorage && !this.storageIsFull
      this.note(this.storageIsFull ? 'storage is full' : 'storage has room')
    } else if (roll < 90) {
      await this.discardANotice()
    } else if (roll < 92) {
      tab.done()
      this.note(`tab ${index} presses Done`)
    } else if (roll < 94) {
      void tab.answerConflict(this.random, this.trace)
      this.note(`tab ${index} answers a conflict`)
    } else if (roll < 95) {
      void tab.refetch()
      this.note(`tab ${index} reads the entry`)
    } else if (roll < 96) {
      tab.resume()
      this.note(`the person sees tab ${index} again`)
    } else if (roll < 98) {
      const row = this.pick(world.rows)
      if (row) {
        const chunk = `c${(this.tokenCount += 1)}x`
        world.appendChunk(row, chunk)
        this.note(`a chunk ${chunk} joins ${row.id}`)
      }
    } else if (hasSignOut) {
      await this.signOut(tab)
    }
  }

  private mod(tab: Tab) {
    return tab.mod
  }

  /** A crash loses what only a page holds, and the person saw the notice about those words before the crash. A hand-over on its way to the tab is in its memory too. */
  private crash(tab: Tab) {
    for (const token of tokensIn(tab.look(() => tab.mod.wordsOfThisTabOnlyInMemory()).join(' '))) this.trace.discarded.add(token)
    for (const notice of tab.notices()) if (notice.kind === 'inHistory') for (const token of tokensIn(notice.words)) this.trace.knownToBeGone.add(token)
    if (tab.page?.saver.hasConflict()) for (const token of tokensIn(tab.page.editor ?? '')) this.trace.knownToBeGone.add(token)
    const onTheWay = [...channels.onTheWay].filter(({ to }) => to.tab === tab).map(({ data }) => JSON.stringify(data)).join(' ')
    for (const token of tokensIn(onTheWay)) this.trace.discarded.add(token)
    tab.kill(this.storage)
    this.removeTab(tab)
  }

  /** The browser freezes a tab, or a frozen tab wakes. At the wake, the tab runs what waited before the person can use it. */
  private async freezeOrWake(tab: Tab) {
    const index = tab.name
    if (tab.isFrozen) {
      tab.wake()
      await vi.advanceTimersByTimeAsync(0)
      this.note(`tab ${index} wakes`)
      return
    }
    tab.freeze()
    this.note(`tab ${index} freezes`)
  }

  /** A second writer through the Draft route, as the iOS client: it reads, types one word somewhere, and saves from the number of the read. After a loss it reads and tries once more. A word of a save that won must stay in the Draft. */
  private async anotherDeviceTypesInto(row: Row) {
    const token = `d${(this.tokenCount += 1)}x`
    this.note(`another device types ${token} into ${row.id}`)
    for (let attempt = 0; attempt < 2; attempt += 1) {
      const read = await world.getSession({ path: { id: row.id } }).catch(() => null)
      if (!read) return
      const body = { text: typedSomewhere(read.rawText, token, this.random), baseRevision: read.rawDraftRevision, savedAt: new Date().toISOString() }
      const reply = await world.saveRaw({ path: { id: row.id }, body }).catch(() => null)
      if (reply?.outcome === 'Won') {
        row.chunks.push(token)
        this.note(`the save of ${token} by another device wins`)
        return
      }
    }
  }

  private async discardANotice() {
    for (const notice of this.tabs.flatMap((other) => other.notices())) {
      if (notice.kind !== 'inHistory' || !this.random.chance(0.5)) continue
      for (const token of tokensIn(notice.words)) this.trace.knownToBeGone.add(token)
      notice.saver.dismissConflict()
    }
    const canDiscard = (notice: Notice) => 'words' in notice && notice.kind !== 'inHistory' && (notice.kind !== 'onlyInMemory' || notice.canDiscard)
    const tab = this.pick(this.tabs.filter((other) => !other.isFrozen && other.notices().some(canDiscard)))
    if (!tab) return
    const notice = this.pick(tab.notices().filter(canDiscard))
    if (!notice || !('words' in notice)) return
    for (const token of tokensIn(notice.words)) this.trace.discarded.add(token)
    tab.run(() => notice.saver.discard())
    this.note(`tab ${tab.name} discards a notice with: ${notice.words}`)
  }

  private async signOut(tab: Tab) {
    const index = tab.name
    const reports = tab.run(() => tab.keys.askOtherTabsToSettle(5000))
    void tab.run(() => tab.mod.settleDraftSavers())
    await vi.advanceTimersByTimeAsync(5000)
    const otherTabs = await reports
    const shown = tab.run(() => tab.mod.unsentWordsOnThisDevice(owner, otherTabs))
    const discards = this.random.chance(0.3)
    if (!discards && shown.some(({ isOnlyInMemory }) => isOnlyInMemory)) {
      this.note(`tab ${index} would sign out, and the dialog names words in memory, so the person stays`)
      return
    }
    if (discards) {
      for (const token of tokensIn(shown.map(({ words }) => words).join(' '))) this.trace.discarded.add(token)
      tab.run(() => {
        for (const { discard } of shown) discard()
      })
    }
    this.note(`tab ${index} signs out and ${discards ? `discards: ${shown.map(({ words }) => words).join(' | ')}` : 'keeps'}`)
    // A notice about History ends at a sign-out. The person saw it, and the Server holds the words.
    for (const other of this.tabs) {
      for (const notice of other.notices()) if (notice.kind === 'inHistory') for (const token of tokensIn(notice.words)) this.trace.knownToBeGone.add(token)
      if (other.page?.saver.hasConflict()) for (const token of tokensIn(other.page.editor ?? '')) this.trace.knownToBeGone.add(token)
    }
    tab.run(() => tab.mod.removeNoticesOfWordsInHistory(owner))
    for (const other of [...this.tabs]) {
      if (other.isFrozen) {
        this.crash(other)
        continue
      }
      other.close()
      this.removeTab(other)
    }
  }

  async play() {
    for (let step = 0; step < stepsInARun; step += 1) {
      await this.step()
      this.check()
    }
    world.isDown = false
    this.storageIsFull = false
    for (const tab of this.tabs) if (tab.isFrozen) tab.wake()
    this.note('the server and storage work again, each tab wakes, and a person opens a page')
    await this.openTab()
    await vi.advanceTimersByTimeAsync(120_000)
    this.check()
    const gone = lastWordsThatLeftTheDraftWithNoNotice(this.tabs, this.storage, this.trace)
    if (gone.length > 0) throw new Error(`Last words that left the Draft with no notice: ${gone.join(', ')}.\nLast steps:\n  ${this.trace.steps.slice(-Number(process.env.MODEL_TRAIL ?? 40)).join('\n  ')}`)
    for (const tab of [...this.tabs]) tab.run(() => tab.mod.stopDraftSavers())
  }

  private check() {
    const vanished = wordsThatVanished(this.tabs, this.storage, this.trace)
    // One tab edits an entry. A frozen tab does not know yet that it lost the entry, and the person cannot type there.
    const editing = this.tabs.filter((tab) => !tab.isFrozen && tab.page?.place === 'here').map((tab) => tab.page?.clientSessionId)
    const twoEditors = editing.filter((id, at) => editing.indexOf(id) !== at)
    const pushedOut = chunksPushedOut()
    const twice = world.rows.flatMap((row) => tokensIn(row.text).filter((token, at, all) => all.indexOf(token) !== at).map((token) => `${token} in ${row.id}`))
    // A deleted word that left the Draft must not come back. A crash can lose a deletion that only the page held, so a word that never left can stay.
    const back: string[] = []
    for (const [token, deletion] of this.trace.deleted) {
      const isInDraft = tokensIn(world.rows.find((row) => row.clientSessionId === deletion.clientSessionId)?.text ?? '').includes(token)
      if (isInDraft && deletion.place === 'left') back.push(token)
      else if (isInDraft) deletion.place = 'inDraft'
      else if (deletion.place === 'inDraft') deletion.place = 'left'
    }
    if (vanished.length === 0 && pushedOut.length === 0 && twice.length === 0 && back.length === 0 && twoEditors.length === 0) return
    const trail = this.trace.steps.slice(-Number(process.env.MODEL_TRAIL ?? 40)).join('\n  ')
    throw new Error(`${vanished.length > 0 ? `Words that vanished: ${vanished.join(', ')}.` : ''} ${pushedOut.length > 0 ? `Words of a recording or another device pushed out of the Draft: ${pushedOut.join(', ')}.` : ''} ${twice.length > 0 ? `Words twice in the Draft: ${twice.join(', ')}.` : ''} ${back.length > 0 ? `Deleted words back in the Draft: ${back.join(', ')}.` : ''} ${twoEditors.length > 0 ? `Two tabs edit: ${twoEditors.join(', ')}.` : ''}\nLast steps:\n  ${trail}`)
  }
}

beforeEach(() => {
  vi.useFakeTimers({ now: new Date('2026-11-07T00:00:00.000Z'), toFake: ['setTimeout', 'clearTimeout', 'setInterval', 'clearInterval', 'Date'] })
  clock.set = globalThis.setTimeout
  clock.clear = globalThis.clearTimeout
  current = null
  vi.stubGlobal('setTimeout', tabTimeout)
  vi.stubGlobal('clearTimeout', tabClearTimeout)
  vi.stubGlobal('queueMicrotask', (work: () => void) => {
    const tab = current
    void Promise.resolve().then(() => asTab(tab, work))
  })
  vi.stubGlobal('BroadcastChannel', ModelChannel)
  vi.stubGlobal('window', { addEventListener: () => undefined, removeEventListener: () => undefined })
  vi.stubGlobal('sessionStorage', storageWith(() => false))
  vi.stubGlobal('navigator', {})
  vi.stubGlobal('document', { documentElement: {}, addEventListener: () => undefined, removeEventListener: () => undefined, visibilityState: 'visible' })
})

afterEach(() => {
  vi.unstubAllGlobals()
  vi.useRealTimers()
  vi.restoreAllMocks()
})

test('each word that a person typed and did not discard is on the server, in a key, in a notice, or on a screen', async () => {
  const seed = Number(process.env.MODEL_SEED ?? defaultSeed)
  // Vitest hides the console of a test that passes.
  process.stdout.write(`draft saver model: seed ${seed}, ${runs} runs of ${stepsInARun} steps. Set MODEL_SEED to run another seed.\n`)
  const failures: string[] = []
  for (let run = 0; run < runs; run += 1) {
    await new ModelRun(seed + run).play().catch((error: unknown) => failures.push(`Run ${run}, seed ${seed + run}: ${error instanceof Error ? error.message : String(error)}`))
    vi.clearAllTimers()
    if (failures.length >= Number(process.env.MODEL_MAX_FAILURES ?? 3)) break
  }
  expect(failures.join('\n\n')).toBe('')
}, 170_000)

function aScene() {
  const storage = storageWith(() => false)
  vi.stubGlobal('localStorage', storage)
  world = new ModelServer()
  channels = new ModelChannels()
  locks = new ModelLocks()
  everyTab.clear()
  return storage
}

/** A tab that opens the entry. It edits once the lock comes, unless another tab edits the entry. */
async function aTabOn(clientSessionId: string, sessionId: string | null = null, baseRevision = 0) {
  const tab = new Tab()
  await tab.load()
  tab.openDraft(clientSessionId, sessionId, baseRevision)
  await vi.advanceTimersByTimeAsync(10)
  return tab
}

/** The same entry in another tab, as a person opens it from the list. */
const aSecondTabOn = (row: Row) => aTabOn(row.clientSessionId, row.id, row.draftRevision)

const pass = (ms: number) => vi.advanceTimersByTimeAsync(ms)
const allKeys = (storage: Storage) => Object.keys(storage).map((key) => storage.getItem(key)).join(' ')

test('a second tab on the entry shows the words of the editor, and edits only after Edit here', async () => {
  aScene()
  const first = await aTabOn('entry-1')
  first.type('w1x')
  await pass(2000)
  const row = world.rows[0] as Row

  const second = await aSecondTabOn(row)
  expect(first.page?.place).toBe('here')
  expect(second.page?.place).toBe('elsewhere')
  expect(second.wordsOfTheEditor()).toBeNull()
  expect(second.type('w2x')).toBe(false)

  world.isDown = true
  first.type('w3x')
  await pass(1000)
  expect(second.wordsOfTheEditor()).toBe('w1x w3x')
  world.isDown = false
  second.editHere()
  await pass(1000)
  expect(first.page?.place).toBe('elsewhere')
  expect(second.page?.place).toBe('here')
  expect(tokensIn(second.page?.editor ?? '')).toEqual(['w1x', 'w3x'])
  expect(first.type('w4x')).toBe(false)
  second.type('w5x')
  await pass(5000)

  expect(tokensIn(row.text)).toEqual(['w1x', 'w3x', 'w5x'])
  expect(second.page?.saver.getSnapshot().conflict).toBeNull()
  first.run(() => first.mod.stopDraftSavers())
  second.run(() => second.mod.stopDraftSavers())
})

for (const state of ['saved', 'waiting to save', 'saving', 'offline'] as const) {
  test(`Edit here while the editor is ${state} moves each word once, with its base`, async () => {
    const storage = aScene()
    world.latencyMs = () => 300
    const first = await aTabOn('entry-1')
    first.type('w1x')
    await pass(3000)
    const row = world.rows[0] as Row
    const second = await aSecondTabOn(row)
    world.appendChunk(row, 'c2x')
    if (state === 'offline') world.isDown = true
    if (state !== 'saved') first.type('w3x')
    if (state === 'saving') await pass(1000)

    second.editHere()
    await pass(600)
    expect(second.page?.place).toBe('here')
    expect(first.page?.place).toBe('elsewhere')
    if (state === 'offline') expect(tokensIn(allKeys(storage))).toContain('w3x')
    world.isDown = false
    second.type('w4x')
    await pass(60_000)

    const words = state === 'saved' ? ['w1x', 'c2x', 'w4x'] : ['w1x', 'c2x', 'w3x', 'w4x']
    expect(tokensIn(row.text)).toEqual(words)
    expect(tokensIn(second.page?.editor ?? '')).toEqual(words)
    expect(second.page?.saver.getSnapshot().conflict).toBeNull()
    expect(tokensIn(allKeys(storage))).toEqual([])
    first.run(() => first.mod.stopDraftSavers())
    second.run(() => second.mod.stopDraftSavers())
  })
}

test('words that a take brings during the read of an older take keep their base, so the words of another device stay', async () => {
  aScene()
  const first = await aTabOn('entry-1')
  first.type('w1x')
  await pass(2000)
  const row = world.rows[0] as Row
  const second = await aSecondTabOn(row)
  // The read of a take waits up to 3 s, and this read answers just before.
  world.readsTakeMs = 2_800
  second.editHere()
  await pass(700)
  expect(second.page?.place).toBe('waiting')

  world.readsTakeMs = null
  world.isDown = true
  first.editHere()
  await pass(700)
  world.appendChunk(row, 'c2x')
  first.type('w3x')
  await pass(500)
  second.editHere()
  await pass(600)
  expect(second.page?.place).toBe('here')
  expect(tokensIn(second.page?.editor ?? '')).toEqual(['w1x', 'w3x'])
  // The read of the first take answers now, with c2x.
  await pass(5_000)
  world.isDown = false
  await pass(60_000)

  expect(tokensIn(row.text)).toEqual(['w1x', 'c2x', 'w3x'])
  expect(tokensIn(second.page?.editor ?? '')).toEqual(['w1x', 'c2x', 'w3x'])
  first.run(() => first.mod.stopDraftSavers())
  second.run(() => second.mod.stopDraftSavers())
})

test('a crash of the editor frees the entry, and the next tab takes its words from storage', async () => {
  const storage = aScene()
  const first = await aTabOn('entry-1')
  first.type('w1x')
  await pass(2000)
  const row = world.rows[0] as Row
  world.isDown = true
  first.type('w2x')
  await pass(1000)
  first.kill(storage)

  world.isDown = false
  const next = await aSecondTabOn(row)
  await pass(1000)
  expect(next.page?.place).toBe('here')
  expect(tokensIn(next.page?.editor ?? '')).toEqual(['w1x', 'w2x'])
  next.type('w3x')
  await pass(60_000)

  expect(tokensIn(row.text)).toEqual(['w1x', 'w2x', 'w3x'])
  expect(tokensIn(allKeys(storage))).toEqual([])
  next.run(() => next.mod.stopDraftSavers())
})

test('a close of the editor gives the entry to a tab that shows it, and that tab edits from the words of the editor', async () => {
  const storage = aScene()
  const first = await aTabOn('entry-1')
  first.type('w1x')
  await pass(2000)
  const row = world.rows[0] as Row
  const second = await aSecondTabOn(row)
  world.isDown = true
  first.type('w2x')
  await pass(1000)
  expect(second.wordsOfTheEditor()).toBe('w1x w2x')

  // The tab that waits in line gets the lock with no click, and it takes the key that the editor left.
  first.close()
  await pass(1000)
  expect(second.page?.place).toBe('here')
  expect(tokensIn(second.page?.editor ?? '')).toEqual(['w1x', 'w2x'])
  world.isDown = false
  await pass(60_000)

  expect(tokensIn(row.text)).toEqual(['w1x', 'w2x'])
  expect(tokensIn(allKeys(storage))).toEqual([])
  second.run(() => second.mod.stopDraftSavers())
})

test('a frozen editor that wakes after a steal shows the words of the new editor, and no word repeats', async () => {
  const storage = aScene()
  const first = await aTabOn('entry-1')
  first.type('w1x')
  await pass(2000)
  const row = world.rows[0] as Row
  world.isDown = true
  first.type('w2x')
  await pass(1000)
  first.freeze()

  const second = await aSecondTabOn(row)
  second.editHere()
  await pass(1000)
  expect(second.page?.place).toBe('here')
  expect(tokensIn(second.page?.editor ?? '')).toEqual(['w1x', 'w2x'])
  second.type('w3x')
  await pass(1000)

  first.wake()
  await pass(1000)
  expect(first.page?.place).toBe('elsewhere')
  expect(first.type('w4x')).toBe(false)
  world.isDown = false
  await pass(60_000)

  expect(tokensIn(row.text)).toEqual(['w1x', 'w2x', 'w3x'])
  expect(tokensIn(allKeys(storage))).toEqual([])
  first.run(() => first.mod.stopDraftSavers())
  second.run(() => second.mod.stopDraftSavers())
})

test('on a full storage, a steal moves the words of the editor over the channel, and the new editor merges them once from their base', async () => {
  let isFull = false
  const storage = storageWith(() => isFull)
  vi.stubGlobal('localStorage', storage)
  world = new ModelServer()
  channels = new ModelChannels()
  locks = new ModelLocks()
  everyTab.clear()
  const first = await aTabOn('entry-1')
  first.type('w1x')
  await pass(2000)
  const row = world.rows[0] as Row
  const second = await aSecondTabOn(row)
  isFull = true
  world.isDown = true
  first.type('w2x')
  await pass(1000)
  expect(tokensIn(allKeys(storage))).not.toContain('w2x')
  world.isDown = false
  world.appendChunk(row, 'c3x')

  second.editHere()
  await pass(1000)
  expect(first.page?.place).toBe('elsewhere')
  expect(second.page?.place).toBe('here')
  second.type('w4x')
  await pass(60_000)

  // Both sides added after w1x, so the other device goes first, then the person, as for each merge.
  expect(tokensIn(row.text)).toEqual(['w1x', 'c3x', 'w2x', 'w4x'])
  expect(tokensIn(second.page?.editor ?? '')).toEqual(['w1x', 'c3x', 'w2x', 'w4x'])
  expect(second.page?.saver.getSnapshot().conflict).toBeNull()
  first.run(() => first.mod.stopDraftSavers())
  second.run(() => second.mod.stopDraftSavers())
})

test('a reload with a second tab open keeps the words on the screen, whichever tab took the key', async () => {
  const storage = aScene()
  world.isDown = true
  const reloading = await aTabOn('entry-1')
  reloading.type('w1x')
  await pass(1000)
  const other = await aTabOn('entry-2')
  await pass(1000)

  reloading.close()
  await pass(1000)
  const back = await aTabOn('entry-1')
  await pass(1000)

  expect(back.page?.place).toBe('here')
  expect(back.page?.editor).toBe('w1x')
  expect(tokensIn(allKeys(storage))).toContain('w1x')
  world.isDown = false
  await pass(60_000)
  expect(tokensIn(world.rows.find(({ clientSessionId }) => clientSessionId === 'entry-1')?.text ?? '')).toEqual(['w1x'])
  back.run(() => back.mod.stopDraftSavers())
  other.run(() => other.mod.stopDraftSavers())
})

test('a discard removes only the words of its notice, also when a frozen tab wakes and types more', async () => {
  const storage = aScene()
  world.isDown = true
  const frozen = await aTabOn('entry-1')
  frozen.type('w1x')
  await pass(1000)
  frozen.freeze()

  const other = await aTabOn('entry-2')
  await pass(1000)
  const notice = other.notices().find((one) => 'words' in one && one.words.includes('w1x'))
  frozen.wake()
  await pass(10)
  frozen.type('w2x')
  await pass(1000)
  other.run(() => notice?.saver.discard())

  expect(tokensIn(allKeys(storage))).toContain('w2x')
  world.isDown = false
  await pass(60_000)
  expect(tokensIn(world.allText())).toContain('w2x')
  frozen.run(() => frozen.mod.stopDraftSavers())
  other.run(() => other.mod.stopDraftSavers())
})

test('a held text that another held text starts with stays, and both reach the server', async () => {
  const storage = aScene()
  world.isDown = true
  const held = (clientSessionId: string, writer: string, words: string) =>
    storage.setItem(
      `recall.unsent.${owner}.${writer}.1`,
      JSON.stringify({ key: `${clientSessionId}.raw`, channel: 'raw', clientSessionId, startedAt: '2026-11-07T00:00:00.000Z', sessionId: null, baseRevision: 0, baseText: '', change: null, held: words }),
    )
  held('entry-1', 'tab-gone-1', 'w1x')
  held('entry-2', 'tab-gone-2', 'w1x w2x')

  const tab = await aTabOn('entry-3')
  await pass(1000)
  world.isDown = false
  await pass(60_000)

  expect(world.rows.map(({ text }) => tokensIn(text).join(' ')).sort()).toEqual(['w1x', 'w1x w2x'])
  tab.run(() => tab.mod.stopDraftSavers())
})

test('a sign-out in one tab names the words that only the memory of another tab holds', async () => {
  let isFull = false
  const storage = storageWith(() => isFull)
  vi.stubGlobal('localStorage', storage)
  world = new ModelServer()
  channels = new ModelChannels()
  locks = new ModelLocks()
  world.isDown = true
  const typing = await aTabOn('entry-1')
  const leaving = await aTabOn('entry-2')
  await pass(1000)
  isFull = true
  typing.type('w1x')
  await pass(1000)

  const reports = leaving.run(() => leaving.keys.askOtherTabsToSettle(2000))
  await pass(5000)
  const shown = leaving.run(() => leaving.mod.unsentWordsOnThisDevice(owner, []))
  const ofOtherTab = leaving.run(() => leaving.mod.unsentWordsOnThisDevice(owner, []))
  const otherTabs = await reports

  expect(shown.concat(ofOtherTab).some(({ words }) => words.includes('w1x'))).toBe(false)
  const named = leaving.run(() => leaving.mod.unsentWordsOnThisDevice(owner, otherTabs))
  expect(named).toEqual(expect.arrayContaining([expect.objectContaining({ words: 'w1x', isOnlyInMemory: true })]))
  typing.run(() => typing.mod.stopDraftSavers())
  leaving.run(() => leaving.mod.stopDraftSavers())
})

test('a discard at sign-out leaves no readable text of the words that it named', async () => {
  const storage = aScene()
  world.isDown = true
  const gone = await aTabOn('entry-1')
  gone.type('w1x')
  await pass(1000)
  gone.close()
  storage.setItem(
    `recall.unsent.${owner}.tab-gone.1`,
    JSON.stringify({ key: 'entry-2.raw', channel: 'raw', clientSessionId: 'entry-2', startedAt: '2026-11-07T00:00:00.000Z', sessionId: null, baseRevision: 0, baseText: '', change: null, held: 'w2x' }),
  )

  const leaving = await aTabOn('entry-3')
  await pass(1000)
  const shown = leaving.run(() => leaving.mod.unsentWordsOnThisDevice(owner, []))
  expect(tokensIn(shown.map(({ words }) => words).join(' '))).toEqual(expect.arrayContaining(['w1x', 'w2x']))
  leaving.run(() => {
    for (const { discard } of shown) discard()
  })

  expect(tokensIn(allKeys(storage))).toEqual([])
  leaving.run(() => leaving.mod.stopDraftSavers())
})

test('a discard at sign-out removes the key of a tab that still lives, with the words that it named', async () => {
  const storage = aScene()
  world.isDown = true
  const live = await aTabOn('entry-1')
  live.type('w1x')
  await pass(1000)

  const leaving = await aTabOn('entry-2')
  await pass(1000)
  const shown = leaving.run(() => leaving.mod.unsentWordsOnThisDevice(owner, []))
  expect(tokensIn(shown.map(({ words }) => words).join(' '))).toEqual(['w1x'])
  leaving.run(() => {
    for (const { discard } of shown) discard()
  })

  expect(tokensIn(allKeys(storage))).toEqual([])
  live.run(() => live.mod.stopDraftSavers())
  leaving.run(() => leaving.mod.stopDraftSavers())
})

test('a lost save whose reply did not come is lost again at the replay, and a merge puts its words and the chunk in the Draft', async () => {
  const storage = aScene()
  const tab = await aTabOn('entry-1')
  tab.type('w1x')
  await pass(2000)
  const row = world.rows[0] as Row
  world.appendChunk(row, 'c2x')
  world.losesNextReply = true
  tab.type('w3x')
  await pass(10_000)

  expect(tokensIn(row.text)).toEqual(['w1x', 'c2x', 'w3x'])
  expect(tokensIn(tab.page?.editor ?? '')).toEqual(['w1x', 'c2x', 'w3x'])
  expect(tab.page?.saver.getSnapshot().conflict).toBeNull()
  expect(tokensIn(allKeys(storage))).toEqual([])
  tab.run(() => tab.mod.stopDraftSavers())
})

test('a won save whose reply did not come raises no notice, and the read gives the base', async () => {
  aScene()
  const tab = await aTabOn('entry-1')
  tab.type('w1x')
  await pass(2000)
  const row = world.rows[0] as Row
  world.appendChunk(row, 'c2x')
  tab.run(() => (tab.page as Page).saver.moveBaseTo(row.draftRevision, row.text))
  world.losesNextReply = true
  tab.type('w3x')
  await pass(10_000)

  expect(tokensIn(row.text)).toContain('w3x')
  expect(tab.page?.saver.getSnapshot().conflict).toBeNull()
  expect(tab.page?.saver.getSnapshot().baseRevision).toBe(row.draftRevision)
  expect(tab.page?.saver.lastSavedWords()).toBe(row.text)
  tab.run(() => tab.mod.stopDraftSavers())
})

test('a merge whose reply did not come finds its words in the Draft at the replay, and nothing repeats', async () => {
  aScene()
  const tab = await aTabOn('entry-1')
  tab.type('w1x')
  await pass(2000)
  const row = world.rows[0] as Row
  world.appendChunk(row, 'c2x')
  tab.type('w3x')
  world.losesNextReply = true
  await pass(1000)
  world.losesNextReply = true
  await pass(10_000)

  expect(tokensIn(row.text)).toEqual(['w1x', 'c2x', 'w3x'])
  expect(tab.page?.saver.getSnapshot().conflict).toBeNull()
  tab.run(() => tab.mod.stopDraftSavers())
})

test('a save that won with no reply, then a chunk and more typing during the read, puts each word in the Draft once', async () => {
  aScene()
  const tab = await aTabOn('entry-1')
  tab.type('w1x')
  await pass(2000)
  const row = world.rows[0] as Row
  world.losesNextReply = true
  world.readsTakeMs = 1500
  tab.type('w2x')
  await pass(1000)
  world.appendChunk(row, 'c3x')
  tab.type('w4x')
  await pass(15_000)

  expect([...tokensIn(row.text)].sort()).toEqual(['c3x', 'w1x', 'w2x', 'w4x'])
  expect([...tokensIn(tab.page?.editor ?? '')].sort()).toEqual(['c3x', 'w1x', 'w2x', 'w4x'])
  expect(tab.page?.saver.getSnapshot().conflict).toBeNull()
  tab.run(() => tab.mod.stopDraftSavers())
})

test('a read after a replay that sees a later save of this page raises no notice of a loss', async () => {
  aScene()
  const tab = await aTabOn('entry-1')
  tab.type('w1x')
  await pass(2000)
  const row = world.rows[0] as Row
  world.losesNextReply = true
  tab.type('w2x')
  await pass(1000)
  tab.type('w3x')
  world.readsTakeMs = 1500
  await pass(10_000)

  expect(tokensIn(row.text)).toContain('w3x')
  expect(tab.page?.saver.getSnapshot().conflict).not.toBe('wordsMovedToHistory')
  tab.run(() => tab.mod.stopDraftSavers())
})

test('a read after a replay that sees a later save before its reply raises no notice of a loss', async () => {
  aScene()
  const tab = await aTabOn('entry-1')
  tab.type('w1x')
  await pass(2000)
  const row = world.rows[0] as Row
  world.losesNextReply = true
  tab.type('w2x')
  await pass(1000)
  tab.type('w3x')
  world.latencyMs = () => 2000
  world.readsTakeMs = 1500
  const saver = (tab.page as Page).saver
  const conflicts: unknown[] = []
  saver.subscribe(() => conflicts.push(saver.getSnapshot().conflict))
  await pass(15_000)

  expect(tokensIn(row.text)).toContain('w3x')
  expect(conflicts).not.toContain('wordsMovedToHistory')
  tab.run(() => tab.mod.stopDraftSavers())
})

test('a discard at sign-out leaves words that the person typed after the list', async () => {
  const storage = aScene()
  world.isDown = true
  const tab = await aTabOn('entry-1')
  tab.type('w1x')
  await pass(1000)
  const shown = tab.run(() => tab.mod.unsentWordsOnThisDevice(owner, []))
  tab.type('w2x')
  await pass(1000)

  tab.run(() => {
    tab.mod.stopDraftSavers()
    for (const { discard } of shown) discard()
  })

  expect(tokensIn(allKeys(storage))).toContain('w2x')
})

test('a saver that took the key of a tab that left and moved to a new entry counts once', async () => {
  const storage = aScene()
  const tab = await aTabOn('entry-1')
  tab.type('w1x')
  await pass(2000)
  const row = world.rows[0] as Row
  storage.setItem(`recall.left.tab-gone`, '1')
  storage.setItem(
    `recall.unsent.${owner}.tab-gone.1`,
    JSON.stringify({ key: 'entry-1.raw', channel: 'raw', clientSessionId: 'entry-1', startedAt: '2026-11-07T00:00:00.000Z', sessionId: row.id, baseRevision: 1, baseText: row.text, change: { value: 'w2x', savedAt: '2026-11-07T00:00:01.000Z' }, held: null }),
  )
  world.rows = []
  world.refusesACreate = true

  tab.resume()
  await pass(40_000)

  const shown = tab.run(() => tab.mod.unsentWordsOnThisDevice(owner, [])).map(({ words }) => words)
  expect(shown.filter((words) => words === 'w2x')).toEqual(['w2x'])
  tab.run(() => tab.mod.stopDraftSavers())
})

test('a save with an old base after a chunk merges with no click: the chunk stays, and each word of the person is in the Draft once', async () => {
  aScene()
  const tab = await aTabOn('entry-1')
  tab.type('w1x')
  await pass(2000)
  const row = world.rows[0] as Row
  world.appendChunk(row, 'c2x')
  tab.type('w3x')
  await pass(2000)
  tab.type('w4x')
  await pass(2000)

  expect(tokensIn(row.text)).toEqual(['w1x', 'c2x', 'w3x', 'w4x'])
  expect(tokensIn(tab.page?.editor ?? '')).toEqual(['w1x', 'c2x', 'w3x', 'w4x'])
  expect(tab.page?.saver.getSnapshot().conflict).toBeNull()
  tab.run(() => tab.mod.stopDraftSavers())
})

for (const paceMs of [1000, 250, 100, 50]) {
  test(`against a writer that saves each ${paceMs} ms, the words of the page are in the Draft once after the writer stops, and the page waits after 3 losses`, async () => {
    aScene()
    world.latencyMs = () => 60
    const tab = await aTabOn('entry-1')
    tab.type('w1x')
    await pass(2000)
    const row = world.rows[0] as Row
    const saves: { at: number; outcome: string }[] = []
    const save = world.saveRaw.bind(world)
    world.saveRaw = async (request) => {
      const at = Date.now()
      const reply = await save(request)
      saves.push({ at, outcome: reply.outcome })
      return reply
    }
    let writes = 0
    const writer = setInterval(() => world.appendChunk(row, `c${(writes += 1)}x`), paceMs)
    for (let word = 2; word <= 7; word += 1) {
      tab.type(`w${word}x`)
      await pass(1000)
    }
    clearInterval(writer)
    await pass(120_000)

    const words = tokensIn(row.text).filter((token) => token.startsWith('w'))
    expect(words).toEqual(['w1x', 'w2x', 'w3x', 'w4x', 'w5x', 'w6x', 'w7x'])
    expect(tokensIn(row.text).filter((token) => token.startsWith('c'))).toHaveLength(writes)
    expect(tab.page?.saver.getSnapshot().conflict).toBeNull()
    let lostInARow = 0
    for (const [at, { outcome }] of saves.entries()) {
      if (at > 0 && lostInARow >= 3) expect(saves[at].at - saves[at - 1].at).toBeGreaterThanOrEqual(Math.min(1000 * 2 ** (lostInARow - 3), 30_000))
      lostInARow = outcome === 'Lost' ? lostInARow + 1 : 0
    }
    expect(saves[saves.length - 1]?.outcome).not.toBe('Lost')
    tab.run(() => tab.mod.stopDraftSavers())
  })
}
