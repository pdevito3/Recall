import { isProblemDetailsError, isStaleVersionError, type ProblemDetailsError } from '@/lib/problem-details'

import type { Stamped } from './save-queue'
import { isFinalRefusal, SerialSaver } from './serial-saver'

export interface FieldAsk<F> {
  field: keyof F & string
  yours: F[keyof F]
  now: F[keyof F]
}

export interface RowBase<F> {
  values: F
  version: number
}

interface Attempt<F> {
  base: RowBase<F>
  hadNoReply: boolean
}

export interface RowSaverState<F> {
  change: Stamped<Partial<F>> | null
  attempt: Attempt<F> | null
  asks: FieldAsk<F>[]
}

export interface StaleRules<F> {
  asksFirst?: (field: keyof F, value: F[keyof F]) => boolean
  neverAsks?: (field: keyof F, value: F[keyof F]) => boolean
  /** The app chose this value and the person did not, so the value of the row wins with no question. */
  yieldsToTheRow?: (field: keyof F, before: F[keyof F]) => boolean
}

interface StaleInput<F> {
  changed: Partial<F>
  before: F
  fresh: F
  hadNoReply: boolean
  rules?: StaleRules<F>
}

export interface StalePlan<F> {
  apply: Partial<F>
  asks: FieldAsk<F>[]
  appliedWithNoAsk: (keyof F & string)[]
}

const isSame = (a: unknown, b: unknown) => JSON.stringify(a ?? null) === JSON.stringify(b ?? null)

const fieldsIn = <F extends object>(change: Partial<F>) => Object.keys(change) as (keyof F & string)[]

/** Steps 4 of "Settings and metadata saves" in docs/ios-api-changes.md: what each changed field does after a stale 409. */
export function planAfterStale<F extends object>({ changed, before, fresh, hadNoReply, rules }: StaleInput<F>): StalePlan<F> {
  const plan: StalePlan<F> = { apply: {}, asks: [], appliedWithNoAsk: [] }
  for (const field of fieldsIn(changed)) {
    const yours = changed[field] as F[keyof F]
    const now = fresh[field]
    if (isSame(yours, now) || rules?.yieldsToTheRow?.(field, before[field])) continue
    // A save with no reply can be a key from days ago, so even a choice that never asks must not go over a later choice.
    if (rules?.neverAsks?.(field, yours) && !hadNoReply) {
      plan.apply[field] = yours as F[typeof field]
      plan.appliedWithNoAsk.push(field)
    } else if (rules?.asksFirst?.(field, yours) || hadNoReply || !isSame(before[field], now)) {
      plan.asks.push({ field, yours, now })
    } else {
      plan.apply[field] = yours as F[typeof field]
    }
  }
  return plan
}

const staleRoundsInOneSend = 3

interface RowSaverOptions<F extends object, R> {
  copyOf: () => R | undefined
  read: (signal: AbortSignal) => Promise<R>
  fieldsOf: (row: R) => F
  versionOf: (row: R) => number
  put: (values: F, version: number, signal: AbortSignal) => Promise<R>
  onReply: (row: R) => void
  rules?: StaleRules<F>
  onAppliedWithNoAsk?: (fields: (keyof F & string)[]) => void
  refusalMessage?: (error: ProblemDetailsError) => string
  onStateChange?: (state: RowSaverState<F> | null) => void
}

const mergeFields = <F extends object>(older: Partial<F>, newer: Partial<F>): Partial<F> => ({ ...older, ...newer })

const withoutFields = <F extends object>(change: Partial<F>, fields: string[]) =>
  Object.fromEntries(Object.entries(change).filter(([field]) => !fields.includes(field))) as Partial<F>

/** The saves of one row with a base version: the queue holds the fields that the person changed, and each body comes from the copy of the row. */
export class RowSaver<F extends object, R> {
  private readonly options: RowSaverOptions<F, R>
  private readonly saver: SerialSaver<Partial<F>, R>
  private attempt: Attempt<F> | null = null
  private currentAsks: FieldAsk<F>[] = []
  /** The time of each answer to keep a field, so that only a later change clears the answer. */
  private readonly keptAsTheyAre = new Map<string, string>()
  private appliedWithNoAsk: (keyof F & string)[] = []
  private readonly listeners = new Set<() => void>()
  private isDisposed = false

  constructor(options: RowSaverOptions<F, R>) {
    this.options = options
    this.saver = new SerialSaver<Partial<F>, R>({
      debounceMs: 0,
      mergeEdits: mergeFields,
      send: ({ value }, signal) =>
        this.send(value, signal).catch((error: unknown) => {
          if (isFinalRefusal(error)) this.appliedWithNoAsk = []
          throw error
        }),
      onReply: (row) => {
        this.options.onReply(row)
        this.showRowInAsks(row)
      },
      refusalMessage: options.refusalMessage,
      onQueueChange: () => this.keep(),
    })
  }

  edit(fields: Partial<F>) {
    this.currentAsks = this.currentAsks.filter((ask) => !(ask.field in fields))
    for (const field of fieldsIn(fields)) this.keptAsTheyAre.delete(field)
    this.appliedWithNoAsk = this.appliedWithNoAsk.filter((field) => !(field in fields))
    this.noteBase()
    this.saver.edit(fields)
    this.notify()
  }

  /** The state of a tab that left joins this saver. Each field that this tab changed later stays, and the oldest base goes with the save, so a choice made since that tab left makes the Server answer stale. */
  adopt({ change, attempt, asks }: RowSaverState<F>) {
    const oldest = [this.attempt, attempt].filter((one) => one !== null).sort((one, other) => one.base.version - other.base.version)[0]
    // A new base outlives the reply of a save that runs now, which has another base.
    if (oldest) this.attempt = { base: { ...oldest.base }, hadNoReply: true }
    this.currentAsks = [...this.currentAsks, ...asks.filter((ask) => !this.currentAsks.some((own) => own.field === ask.field))]
    const newest = this.saver.newestUnsettled()
    const isOlder = newest !== null && change !== null && newest.savedAt > change.savedAt
    const fields = change && (isOlder ? withoutFields(change.value, fieldsIn(this.pendingFields())) : change.value)
    for (const field of fieldsIn(fields ?? {})) {
      if (change && change.savedAt > (this.keptAsTheyAre.get(field) ?? '')) this.keptAsTheyAre.delete(field)
    }
    if (fields && change && fieldsIn(fields).length > 0) this.saver.edit(fields, isOlder ? newest.savedAt : change.savedAt)
    else this.keep()
    this.notify()
    if (asks.length > 0) void this.readRowForAsks()
  }

  /** The "now" of a stale key is old, and an answer must not replace a value that the person never saw. */
  private async readRowForAsks() {
    const row = await this.options.read(new AbortController().signal).catch(() => null)
    if (row !== null) this.showRowInAsks(row)
  }

  private showRowInAsks(row: R) {
    if (this.isDisposed || this.currentAsks.length === 0) return
    const fresh = this.options.fieldsOf(row)
    this.currentAsks = this.currentAsks.flatMap((ask) => (isSame(ask.yours, fresh[ask.field]) ? [] : { ...ask, now: fresh[ask.field] }))
    this.keep()
    this.notify()
  }

  answerAsk(ask: FieldAsk<F>, usesYours: boolean) {
    this.currentAsks = this.currentAsks.filter((other) => other !== ask)
    if (usesYours) this.edit({ [ask.field]: ask.yours } as Partial<F>)
    else this.keptAsTheyAre.set(ask.field, new Date().toISOString())
    this.keep()
    this.notify()
  }

  asks = () => this.currentAsks

  pendingFields(): Partial<F> {
    const pending = this.saver.unsettled().reduce<Partial<F>>((fields, { value }) => ({ ...fields, ...value }), {})
    return withoutFields(pending, [...this.keptAsTheyAre.keys()])
  }

  hasUnsaved() {
    return !this.isDisposed && (this.currentAsks.length > 0 || fieldsIn(this.pendingFields()).length > 0)
  }

  settle = () => this.saver.settle()

  dispose() {
    this.isDisposed = true
    this.saver.dispose()
  }

  subscribe = (listener: () => void) => {
    this.listeners.add(listener)
    const stopStatus = this.saver.subscribe(listener)
    return () => {
      this.listeners.delete(listener)
      stopStatus()
    }
  }

  getStatus = () => this.saver.getStatus()

  private notify() {
    for (const listener of this.listeners) listener()
  }

  /** The base is the row that the person saw at the change. A later read must not be the base, or a stale key would replace a later choice with no conflict. */
  private noteBase() {
    const copy = this.options.copyOf()
    if (copy !== undefined) this.attempt ??= { base: this.baseOf(copy), hadNoReply: false }
  }

  private baseOf(row: R): RowBase<F> {
    return { values: this.options.fieldsOf(row), version: this.options.versionOf(row) }
  }

  private async send(queued: Partial<F>, signal: AbortSignal): Promise<R> {
    let fields = withoutFields(queued, [...this.currentAsks.map((ask) => ask.field), ...this.keptAsTheyAre.keys()])
    if (fieldsIn(fields).length === 0) return this.options.copyOf() ?? this.options.read(signal)
    this.attempt ??= { base: this.baseOf(this.options.copyOf() ?? (await this.options.read(signal))), hadNoReply: false }
    let { base, hadNoReply } = this.attempt
    this.keep()
    for (let round = 0; round < staleRoundsInOneSend; round += 1) {
      try {
        const reply = await this.options.put({ ...base.values, ...fields }, base.version, signal)
        if (this.attempt?.base === base) this.attempt = null
        this.tellAppliedWithNoAsk()
        return reply
      } catch (error) {
        if (!isStaleVersionError(error)) {
          if (!isProblemDetailsError(error) || (error.status ?? 500) >= 500) this.attempt = { base, hadNoReply: true }
          throw error
        }
      }
      const fresh = await this.options.read(signal)
      if (this.isDisposed) throw new Error('This save stopped.')
      const plan = planAfterStale({ changed: fields, before: base.values, fresh: this.options.fieldsOf(fresh), hadNoReply, rules: this.options.rules })
      if (plan.asks.length > 0) this.addAsks(plan.asks)
      const pending = this.pendingFields()
      this.appliedWithNoAsk.push(...plan.appliedWithNoAsk.filter((field) => !(field in pending) || isSame(pending[field], fields[field])))
      base = this.baseOf(fresh)
      hadNoReply = false
      fields = plan.apply
      this.attempt = { base, hadNoReply }
      if (fieldsIn(fields).length === 0) {
        this.attempt = null
        this.tellAppliedWithNoAsk()
        return fresh
      }
    }
    throw new Error('The row changed again during each try.')
  }

  private tellAppliedWithNoAsk() {
    if (this.appliedWithNoAsk.length > 0) this.options.onAppliedWithNoAsk?.(this.appliedWithNoAsk)
    this.appliedWithNoAsk = []
  }

  /** The person can have changed the field again while the save ran, and the ask must offer that newest value. */
  private addAsks(asks: FieldAsk<F>[]) {
    const pending = this.pendingFields()
    const newest = asks.map((ask) => (ask.field in pending ? { ...ask, yours: pending[ask.field] as F[keyof F] } : ask))
    this.currentAsks = [...this.currentAsks.filter((old) => !asks.some((ask) => ask.field === old.field)), ...newest]
    this.notify()
  }

  private keep() {
    const { onStateChange } = this.options
    if (!onStateChange) return
    const newest = this.saver.newestUnsettled()
    const isRefused = this.saver.refusalMessage() !== null
    const change = newest && !isRefused ? { value: this.pendingFields(), savedAt: newest.savedAt } : null
    onStateChange(change || this.currentAsks.length > 0 ? { change, attempt: change ? this.attempt : null, asks: this.currentAsks } : null)
  }
}
