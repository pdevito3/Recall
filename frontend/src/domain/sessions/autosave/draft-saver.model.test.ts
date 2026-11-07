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
  revisions: { text: string; claimedAt: string; tookAt: number | null }[]
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
    row.revisions.push({ text: save.text, claimedAt: save.savedAt, tookAt: wins ? number : null })
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
    row.text = row.text ? `${row.text} ${chunk}` : chunk
    row.revisions.push({ text: row.text, claimedAt: 'chunk', tookAt: number })
    row.savedAt = new Date().toISOString()
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
type TabKeysModule = typeof import('./tab-keys')
type Saver = ReturnType<DraftSaverModule['draftSaverFor']>
type Notice = ReturnType<DraftSaverModule['draftNotices']>[number]

interface Page {
  saver: Saver
  stopListening: () => void
  clientSessionId: string
  editor: string | null
  held: string | null
}

const tokensIn = (text: string): string[] => text.match(/\b[wc]\d+x\b/g) ?? []

class Tab {
  mod!: DraftSaverModule
  keys!: TabKeysModule
  queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  page: Page | null = null
  isClosed = false
  tokenCount = 0

  async load() {
    channels.tabRunning = this
    vi.resetModules()
    this.mod = await import('./draft-saver')
    this.keys = await import('./tab-keys')
    channels.tabRunning = this
    this.keys.joinTheOtherTabs(async () => {
      await Promise.race([this.mod.settleDraftSavers(), delay(4000)])
      return { wordsOnlyInMemory: this.mod.wordsOfThisTabOnlyInMemory(), hasUnsavedTags: false }
    })
    this.keys.watchOtherTabs(() => this.resume())
    channels.tabRunning = null
  }

  /** The effect of the root route: a load, a tab that left, a tab that the person sees again, or a network that returns. */
  resume() {
    if (!this.isClosed) void this.run(() => this.mod.resumeUnsentDrafts(owner, this.queryClient))
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
    const target = { owner, channel: 'raw' as const, clientSessionId, startedAt: '2026-11-07T00:00:00.000Z', sessionId, baseRevision }
    this.run(() => {
      const saver = this.mod.draftSaverFor(target, this.queryClient)
      const stopListening = saver.subscribe(() => undefined)
      const kept = saver.wordsToOpen()
      const id = saver.getSnapshot().sessionId
      const page: Page = { saver, stopListening, clientSessionId, editor: kept ?? (id ? null : ''), held: saver.wordsInHold() }
      saver.subscribeToNewWords((words) => {
        page.editor = words
      })
      this.page = page
      if (page.editor === null) {
        saver.takeHold()
        void this.readInto(page, id as string)
      }
    })
    this.resume()
  }

  private async readInto(page: Page, id: string) {
    for (;;) {
      const session = await world.getSession({ path: { id } }).catch((error: unknown) => (error instanceof ProblemDetailsError && error.status === 404 ? ('gone' as const) : null))
      if (this.isClosed || this.page !== page || page.editor !== null) return
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
    if (!page || page.editor === null || page.saver.hasConflict()) return
    this.run(() => {
      page.saver.flush()
      page.stopListening()
    })
    this.page = null
  }

  /** Show the newest words replaces the words of the notice on the page, so the person knows that those words leave the Draft. */
  async answerConflict(random: Random, trace: Trace) {
    const page = this.page
    if (!page || page.editor === null || page.saver.getSnapshot().conflict === null) return
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
    if (!page || !id || page.editor === null) return
    const session = await world.getSession({ path: { id } }).catch(() => null)
    if (!session || this.isClosed || this.page !== page) return
    this.run(() => {
      const { baseRevision, conflict } = page.saver.getSnapshot()
      if (session.rawDraftRevision <= baseRevision || page.saver.unsettledText() !== null || conflict !== null) return
      page.saver.moveBaseTo(session.rawDraftRevision, session.rawText)
      page.editor = session.rawText
    })
  }

  notices(): Notice[] {
    return this.run(() => this.mod.draftNotices())
  }

  /** A close that a person makes: the page hears `pagehide`, and its timers and listeners end. */
  close() {
    this.run(() => {
      this.keys.noteThisTabLeaves()
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
  /** The newest word that each tab typed into each entry. */
  lastTyped: Map<string, { token: string; clientSessionId: string }>
  /** A notice named these words, and the person answered it. */
  knownToBeGone: Set<string>
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
  private readonly trace: Trace = { steps: [], discarded: new Set(), typed: new Set(), lastTyped: new Map(), knownToBeGone: new Set() }
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
      const entry = tab.page?.clientSessionId as string
      this.trace.lastTyped.set(`${this.tabNames.get(tab) ?? this.nameTab(tab)} ${entry}`, { token, clientSessionId: entry })
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
      // A crash loses what only a page holds, and the person saw the notice about those words before the crash.
      for (const token of tokensIn(tab.run(() => tab.mod.wordsOfThisTabOnlyInMemory()).join(' '))) this.trace.discarded.add(token)
      for (const notice of tab.notices()) if (notice.kind === 'inHistory') for (const token of tokensIn(notice.words)) this.trace.knownToBeGone.add(token)
      if (tab.page?.saver.hasConflict()) for (const token of tokensIn(tab.page.editor ?? '')) this.trace.knownToBeGone.add(token)
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

  private async discardANotice() {
    for (const notice of this.tabs.flatMap((other) => other.notices())) {
      if (notice.kind !== 'inHistory' || !this.random.chance(0.5)) continue
      for (const token of tokensIn(notice.words)) this.trace.knownToBeGone.add(token)
      notice.saver.dismissConflict()
    }
    const canDiscard = (notice: Notice) => 'words' in notice && notice.kind !== 'inHistory' && (notice.kind !== 'onlyInMemory' || notice.canDiscard)
    const tab = this.pick(this.tabs.filter((other) => other.notices().some(canDiscard)))
    if (!tab) return
    const notice = this.pick(tab.notices().filter(canDiscard))
    if (!notice || !('words' in notice)) return
    for (const token of tokensIn(notice.words)) this.trace.discarded.add(token)
    tab.run(() => notice.saver.discard())
    this.note(`tab ${this.tabs.indexOf(tab)} discards a notice with: ${notice.words}`)
  }

  private async signOut(tab: Tab) {
    const index = this.tabs.indexOf(tab)
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
    const gone = lastWordsThatLeftTheDraftWithNoNotice(this.tabs, this.storage, this.trace)
    if (gone.length > 0) throw new Error(`Last words that left the Draft with no notice: ${gone.join(', ')}.\nLast steps:\n  ${this.trace.steps.slice(-Number(process.env.MODEL_TRAIL ?? 40)).join('\n  ')}`)
    for (const tab of [...this.tabs]) tab.run(() => tab.mod.stopDraftSavers())
  }

  private check() {
    const vanished = wordsThatVanished(this.tabs, this.storage, this.trace)
    const pushedOut = chunksPushedOut()
    if (vanished.length === 0 && pushedOut.length === 0) return
    const trail = this.trace.steps.slice(-Number(process.env.MODEL_TRAIL ?? 40)).join('\n  ')
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
  return storage
}

async function aTabOn(clientSessionId: string) {
  const tab = new Tab()
  await tab.load()
  tab.openDraft(clientSessionId, null, 0)
  return tab
}

const pass = (ms: number) => vi.advanceTimersByTimeAsync(ms)
const allKeys = (storage: Storage) => Object.keys(storage).map((key) => storage.getItem(key)).join(' ')

test('a tab that opens leaves the key of a live tab and shows its words, so the words of both tabs reach the server', async () => {
  const storage = aScene()
  world.isDown = true
  const first = await aTabOn('entry-1')
  first.type('w1x')
  await pass(1000)
  const keysBefore = Object.keys(storage)

  const second = await aTabOn('entry-1')
  await pass(1000)

  expect(Object.keys(storage)).toEqual(expect.arrayContaining(keysBefore))
  expect(second.page?.editor).toBe('w1x')
  first.type('w2x')
  second.type('w3x')
  await pass(1000)
  world.isDown = false
  await pass(60_000)
  expect(tokensIn(world.allText())).toEqual(expect.arrayContaining(['w1x', 'w2x', 'w3x']))
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

  expect(back.page?.editor).toBe('w1x')
  expect(tokensIn(allKeys(storage))).toContain('w1x')
  world.isDown = false
  await pass(60_000)
  expect(tokensIn(world.rows.find(({ clientSessionId }) => clientSessionId === 'entry-1')?.text ?? '')).toEqual(['w1x'])
  back.run(() => back.mod.stopDraftSavers())
  other.run(() => other.mod.stopDraftSavers())
})

test('of two tabs on one entry, the tab that loses merges, so the words of both tabs are in the Draft once, with no notice', async () => {
  aScene()
  const first = await aTabOn('entry-1')
  first.type('w1x')
  await pass(2000)
  const row = world.rows[0] as Row
  const second = await aTabOn('entry-1')
  await pass(2000)
  world.latencyMs = () => 10
  first.type('w2x')
  second.type('w3x')
  await pass(3000)

  expect([...tokensIn(row.text)].sort()).toEqual(['w1x', 'w2x', 'w3x'])
  expect(first.page?.saver.getSnapshot().conflict).toBeNull()
  expect(second.page?.saver.getSnapshot().conflict).toBeNull()
  first.run(() => first.mod.stopDraftSavers())
  second.run(() => second.mod.stopDraftSavers())
})

test('a discard removes only the words of its notice, also when a frozen tab wakes and types more', async () => {
  const storage = aScene()
  world.isDown = true
  const frozen = await aTabOn('entry-1')
  frozen.type('w1x')
  await pass(1000)
  channels.closeChannelsOf(frozen)

  const other = await aTabOn('entry-2')
  await pass(1000)
  const notice = other.notices().find((one) => 'words' in one && one.words.includes('w1x'))
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
      JSON.stringify({ key: `${clientSessionId}.raw`, channel: 'raw', clientSessionId, startedAt: '2026-11-07T00:00:00.000Z', sessionId: null, baseRevision: 0, change: null, held: words }),
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
    JSON.stringify({ key: 'entry-2.raw', channel: 'raw', clientSessionId: 'entry-2', startedAt: '2026-11-07T00:00:00.000Z', sessionId: null, baseRevision: 0, change: null, held: 'w2x' }),
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
    JSON.stringify({ key: 'entry-1.raw', channel: 'raw', clientSessionId: 'entry-1', startedAt: '2026-11-07T00:00:00.000Z', sessionId: row.id, baseRevision: 1, change: { value: 'w2x', savedAt: '2026-11-07T00:00:01.000Z' }, held: null }),
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
