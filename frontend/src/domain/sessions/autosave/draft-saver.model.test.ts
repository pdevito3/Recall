import { QueryClient } from '@tanstack/react-query'
import { afterEach, beforeEach, expect, test, vi } from 'vitest'

import { ProblemDetailsError } from '@/lib/problem-details'

vi.mock('@/api/generated/sessions/clients/add-session', () => ({ addSession: (request: never) => world.addSession(request) }))
vi.mock('@/api/generated/sessions/clients/save-raw-draft', () => ({ saveRawDraft: (request: never) => world.saveRaw(request) }))
vi.mock('@/api/generated/sessions/clients/save-cleaned-draft', () => ({ saveCleanedDraft: () => Promise.reject(new Error('The model has no cleaned channel.')) }))
vi.mock('@/api/generated/sessions/clients/get-session', () => ({ getSession: (request: never) => world.getSession(request) }))
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
  revisions: { text: string; claimedAt: string }[]
  chunks: string[]
}

const delay = (ms: number) => new Promise<void>((resolve) => setTimeout(resolve, ms))

/** The rules of `DraftSlot.Decide` and `Session.SaveRaw` on the Server. A request reaches the Server and answers later, also when its tab is gone. */
class ModelServer {
  rows: Row[] = []
  isDown = false
  latencyMs = () => 50

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
    if (!row) throw new ProblemDetailsError({ type: 'about:blank', title: 'Gone', status: 404 })
    const reply = this.decide(row, body)
    await delay(latency / 2)
    return reply
  }

  private decide(row: Row, save: { text: string; baseRevision: number; savedAt: string }) {
    const answer = (outcome: string) => ({ outcome, draftRevision: row.draftRevision, latestRevision: row.latestRevision, savedAt: row.savedAt ?? save.savedAt, cleanupStatus: 'NotRun', version: row.latestRevision })
    if (save.text === row.text) return answer('Unchanged')
    if (save.baseRevision !== row.draftRevision && row.revisions.some((old) => old.claimedAt === save.savedAt && old.text === save.text)) return answer('Unchanged')
    const number = row.latestRevision + 1
    row.revisions.push({ text: save.text, claimedAt: save.savedAt })
    row.latestRevision = number
    const missesChunk = save.baseRevision < row.lastChunkRevision
    const beats = save.baseRevision === row.draftRevision || row.savedAt === null || save.savedAt >= row.savedAt
    if (missesChunk || !beats) return answer('Lost')
    row.text = save.text
    row.draftRevision = number
    row.savedAt = row.savedAt !== null && row.savedAt > save.savedAt ? row.savedAt : save.savedAt
    return answer('Won')
  }

  async getSession({ path }: { path: { id: string } }) {
    const latency = this.latencyMs()
    await delay(latency)
    const row = this.rows.find((other) => other.id === path.id)
    if (!row) throw new ProblemDetailsError({ type: 'about:blank', title: 'Gone', status: 404 })
    return this.dto(row)
  }

  appendChunk(row: Row, chunk: string) {
    const number = row.latestRevision + 1
    row.text = row.text ? `${row.text} ${chunk}` : chunk
    row.revisions.push({ text: row.text, claimedAt: 'chunk' })
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
  const api = {
    getItem: (key: string) => values.get(key) ?? null,
    setItem: (key: string, value: string) => {
      if (isFull()) throw new DOMException('full', 'QuotaExceededError')
      values.set(key, String(value))
    },
    removeItem: (key: string) => void values.delete(key),
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

/** A channel belongs to the tab that runs when the channel opens, so that a closed tab hears nothing. */
class ModelChannels {
  private open = new Set<ModelChannel>()
  tabRunning: object | null = null

  add(channel: ModelChannel) {
    this.open.add(channel)
  }

  remove(channel: ModelChannel) {
    this.open.delete(channel)
  }

  closeChannelsOf(tab: object) {
    for (const channel of [...this.open]) if (channel.tab === tab) this.open.delete(channel)
  }

  post(from: ModelChannel, data: unknown) {
    for (const channel of [...this.open]) {
      if (channel === from || channel.name !== from.name) continue
      setTimeout(() => {
        if (this.open.has(channel)) channel.onmessage?.({ data } as MessageEvent)
      }, 1)
    }
  }
}

let channels = new ModelChannels()

class ModelChannel {
  readonly name: string
  readonly tab: object | null
  onmessage: ((event: MessageEvent) => void) | null = null

  constructor(name: string) {
    this.name = name
    this.tab = channels.tabRunning
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
type Saver = ReturnType<DraftSaverModule['draftSaverFor']>
type Notice = ReturnType<DraftSaverModule['draftNotices']>[number]

interface Page {
  saver: Saver
  stopListening: () => void
  clientSessionId: string
  editor: string | null
  held: string | null
}

const tokensIn = (text: string) => text.match(/\b[wc]\d+x\b/g) ?? []

class Tab {
  mod!: DraftSaverModule
  queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  page: Page | null = null
  isClosed = false
  tokenCount = 0

  async load() {
    channels.tabRunning = this
    vi.resetModules()
    this.mod = await import('./draft-saver')
    channels.tabRunning = null
  }

  /** Runs code of this tab, so that a channel that it opens is the channel of this tab. */
  run<T>(work: () => T): T {
    channels.tabRunning = this
    try {
      return work()
    } finally {
      channels.tabRunning = null
    }
  }

  openDraft(clientSessionId: string, sessionId: string | null, baseRevision: number) {
    const target = { owner, channel: 'raw' as const, clientSessionId, startedAt: '2026-11-07T00:00:00.000Z', sessionId, baseRevision, latestRevision: baseRevision }
    this.run(() => {
      const saver = this.mod.draftSaverFor(target, this.queryClient)
      const stopListening = saver.subscribe(() => undefined)
      const kept = saver.wordsToOpen()
      const id = saver.getSnapshot().sessionId
      const page: Page = { saver, stopListening, clientSessionId, editor: kept ?? (id ? null : ''), held: saver.wordsInHold() }
      this.page = page
      if (page.editor === null) {
        saver.takeHold()
        void this.readInto(page, id as string)
      }
      this.mod.resumeUnsentDrafts(owner, this.queryClient)
    })
  }

  private async readInto(page: Page, id: string) {
    for (;;) {
      const session = await world.getSession({ path: { id } }).catch((error: unknown) => (error instanceof ProblemDetailsError && error.status === 404 ? ('gone' as const) : null))
      if (this.isClosed || this.page !== page) return
      if (session === null) {
        await delay(2000)
        continue
      }
      this.run(() => {
        if (session === 'gone') {
          page.editor = page.saver.joinHoldInto('')
          return
        }
        page.saver.moveBaseTo(session.rawDraftRevision, session.rawLatestRevision)
        page.editor = page.saver.joinHoldInto(session.rawText)
      })
      return
    }
  }

  type(token: string) {
    const page = this.page
    if (!page) return
    this.run(() => {
      if (page.editor === null) {
        page.held = page.held === null ? token : `${page.held} ${token}`
        page.saver.holdInOutbox(page.held)
        return
      }
      page.editor = page.editor ? `${page.editor} ${token}` : token
      page.saver.edit(page.editor)
    })
  }

  done() {
    const page = this.page
    if (!page || page.editor === null) return
    this.run(() => {
      page.saver.flush()
      page.stopListening()
    })
    this.page = null
  }

  async answerConflict(random: Random) {
    const page = this.page
    if (!page || page.editor === null || page.saver.getSnapshot().conflict !== 'wordsMovedToHistory') return
    if (random.chance(0.5)) {
      const newest = await this.run(() => page.saver.takeNewestWords())
      if (newest !== null && this.page === page) page.editor = newest
      return
    }
    const kept: unknown = await this.run(() => page.saver.keepWords(page.editor as string))
    if (typeof kept === 'string' && this.page === page) page.editor = kept
  }

  /** The effect of an open entry page: a newer Draft replaces the words on the screen when nothing is unsaved. */
  async refetch() {
    const page = this.page
    const id = page?.saver.getSnapshot().sessionId
    if (!page || !id || page.editor === null) return
    const session = await world.getSession({ path: { id } }).catch(() => null)
    if (!session || this.isClosed || this.page !== page) return
    this.run(() => {
      const { baseRevision, conflict } = page.saver.getSnapshot()
      if (session.rawDraftRevision <= baseRevision || page.saver.unsettledText() !== null || conflict === 'wordsMovedToHistory') return
      page.saver.moveBaseTo(session.rawDraftRevision, session.rawLatestRevision)
      page.editor = session.rawText
    })
  }

  notices(): Notice[] {
    return this.run(() => this.mod.draftNotices())
  }

  /** A close that a person makes: the page hears `pagehide`, and its timers and listeners end. */
  close() {
    this.run(() => {
      ;(this.mod as { noteThisTabLeaves?: () => void }).noteThisTabLeaves?.()
      this.mod.stopDraftSavers()
    })
    this.isClosed = true
    channels.closeChannelsOf(this)
  }

  /** A crash: no `pagehide`, and no code of this tab writes to storage again. */
  kill(storage: Storage) {
    const before = Object.keys(storage).map((key) => [key, storage.getItem(key) as string] as const)
    this.run(() => this.mod.stopDraftSavers())
    for (const key of Object.keys(storage)) storage.removeItem(key)
    for (const [key, value] of before) storage.setItem(key, value)
    this.isClosed = true
    channels.closeChannelsOf(this)
  }
}

interface Trace {
  steps: string[]
  discarded: Set<string>
  typed: Set<string>
}

function wordsThatVanished(tabs: Tab[], storage: Storage, trace: Trace) {
  const keys = Object.keys(storage).map((key) => storage.getItem(key)).join(' ')
  const notices = tabs.flatMap((tab) => tab.notices()).map((notice) => ('words' in notice ? notice.words : '')).join(' ')
  const screens = tabs.map((tab) => tab.page?.editor ?? tab.page?.held ?? '').join(' ')
  const present = new Set(tokensIn(`${world.allText()} ${keys} ${notices} ${screens}`))
  return [...trace.typed].filter((token) => !present.has(token) && !trace.discarded.has(token))
}

const chunksPushedOut = () => world.rows.flatMap((row) => row.chunks.filter((chunk) => !row.text.includes(chunk)).map((chunk) => `${chunk} in ${row.id}`))

class ModelRun {
  private readonly random: Random
  private readonly tabs: Tab[] = []
  private readonly trace: Trace = { steps: [], discarded: new Set(), typed: new Set() }
  private storageIsFull = false
  private readonly storage = storageWith(() => this.storageIsFull)
  private readonly pointers = new Map<Tab, string>()
  private tokenCount = 0

  constructor(seed: number) {
    this.random = randomFrom(seed)
    vi.stubGlobal('localStorage', this.storage)
    world = new ModelServer()
    world.latencyMs = () => 20 + this.random.below(1500)
    channels = new ModelChannels()
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

  private async openTab(clientSessionId = this.newEntryId()) {
    const tab = new Tab()
    await tab.load()
    this.tabs.push(tab)
    const row = world.rows.find((other) => other.clientSessionId === clientSessionId)
    tab.openDraft(clientSessionId, row?.id ?? null, row?.draftRevision ?? 0)
    this.pointers.set(tab, clientSessionId)
    this.note(`open tab ${this.tabs.indexOf(tab)} on ${clientSessionId}`)
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
    const index = this.tabs.indexOf(tab)
    if (roll < 40) {
      const token = `w${(this.tokenCount += 1)}x`
      if (!tab.page) tab.openDraft(this.newEntryId(), null, 0)
      this.trace.typed.add(token)
      tab.type(token)
      this.note(`tab ${index} types ${token}`)
    } else if (roll < 62) {
      const ms = this.random.below(2500)
      await vi.advanceTimersByTimeAsync(ms)
      this.note(`${ms} ms pass`)
    } else if (roll < 68) {
      if (this.tabs.length < 3) {
        const same = this.random.chance(0.5) ? tab.page?.clientSessionId : undefined
        await this.openTab(same)
      }
    } else if (roll < 73) {
      if (tab.run(() => this.mod(tab).isAnyWordOnlyInMemory())) {
        this.note(`tab ${index} would close, and the browser warns, so the person stays`)
      } else {
        tab.close()
        this.removeTab(tab)
        this.note(`tab ${index} closes`)
      }
    } else if (roll < 76) {
      tab.kill(this.storage)
      this.removeTab(tab)
      this.note(`tab ${index} crashes`)
    } else if (roll < 81) {
      if (tab.run(() => this.mod(tab).isAnyWordOnlyInMemory())) {
        this.note(`tab ${index} would reload, and the browser warns, so the person stays`)
      } else {
        const pointer = tab.page?.clientSessionId
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
      void tab.answerConflict(this.random)
      this.note(`tab ${index} answers a conflict`)
    } else if (roll < 96) {
      void tab.refetch()
      this.note(`tab ${index} reads the entry`)
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

  private async discardANotice() {
    const tab = this.pick(this.tabs.filter((other) => other.notices().some((notice) => notice.kind !== 'moved' && (notice.kind !== 'onlyInMemory' || notice.canDiscard))))
    if (!tab) return
    const notice = this.pick(tab.notices().filter((other) => other.kind !== 'moved' && (other.kind !== 'onlyInMemory' || other.canDiscard)))
    if (!notice || !('words' in notice)) return
    for (const token of tokensIn(notice.words)) this.trace.discarded.add(token)
    tab.run(() => notice.saver.discard())
    this.note(`tab ${this.tabs.indexOf(tab)} discards a notice with: ${notice.words}`)
  }

  private async signOut(tab: Tab) {
    const settled = tab.run(() => tab.mod.settleDraftSavers())
    const limit = delay(5000).then(() => false)
    await vi.advanceTimersByTimeAsync(5000)
    const isSent = await Promise.race([settled, limit])
    if (!isSent && this.random.chance(0.3)) {
      const named = (tab.mod as { wordsNamedAtSignOut?: (owner: string) => string[] }).wordsNamedAtSignOut?.(owner) ?? []
      for (const token of tokensIn(named.join(' '))) this.trace.discarded.add(token)
      tab.run(() => tab.mod.discardUnsentDrafts())
      this.note(`tab ${this.tabs.indexOf(tab)} signs out and discards: ${named.join(' | ')}`)
    } else {
      this.note(`tab ${this.tabs.indexOf(tab)} signs out and keeps`)
    }
    for (const other of [...this.tabs]) {
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
    this.note('the server and storage work again, and a person opens a page')
    await this.openTab()
    await vi.advanceTimersByTimeAsync(120_000)
    this.check()
    for (const tab of [...this.tabs]) tab.run(() => tab.mod.stopDraftSavers())
  }

  private check() {
    const vanished = wordsThatVanished(this.tabs, this.storage, this.trace)
    const pushedOut = chunksPushedOut()
    if (vanished.length === 0 && pushedOut.length === 0) return
    const trail = this.trace.steps.slice(-40).join('\n  ')
    throw new Error(`${vanished.length > 0 ? `Words that vanished: ${vanished.join(', ')}.` : ''} ${pushedOut.length > 0 ? `Chunks pushed out of the Draft: ${pushedOut.join(', ')}.` : ''}\nLast steps:\n  ${trail}`)
  }
}

beforeEach(() => {
  vi.useFakeTimers({ now: new Date('2026-11-07T00:00:00.000Z'), toFake: ['setTimeout', 'clearTimeout', 'setInterval', 'clearInterval', 'Date'] })
  vi.stubGlobal('BroadcastChannel', ModelChannel)
  vi.stubGlobal('window', { addEventListener: () => undefined, removeEventListener: () => undefined })
  vi.stubGlobal('sessionStorage', storageWith(() => false))
  vi.stubGlobal('navigator', {})
  vi.stubGlobal('document', { documentElement: {}, addEventListener: () => undefined, removeEventListener: () => undefined, visibilityState: 'visible' })
})

afterEach(() => {
  vi.useRealTimers()
  vi.unstubAllGlobals()
})

test('each word that a person typed and did not discard is on the server, in a key, in a notice, or on a screen', async () => {
  const seed = Number(process.env.MODEL_SEED ?? defaultSeed)
  console.log(`draft saver model: seed ${seed}, ${runs} runs of ${stepsInARun} steps. Set MODEL_SEED to run another seed.`)
  const failures: string[] = []
  for (let run = 0; run < runs; run += 1) {
    await new ModelRun(seed + run).play().catch((error: unknown) => failures.push(`Run ${run}, seed ${seed + run}: ${error instanceof Error ? error.message : String(error)}`))
    vi.clearAllTimers()
    if (failures.length >= Number(process.env.MODEL_MAX_FAILURES ?? 3)) break
  }
  expect(failures.join('\n\n')).toBe('')
}, 170_000)
