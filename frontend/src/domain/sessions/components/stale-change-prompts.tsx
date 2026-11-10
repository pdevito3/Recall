import { Button } from '@/components/ui/button'

import type { FieldAsk } from '../autosave/row-saver'

function valueText(value: unknown): string {
  if (value === true) return 'on'
  if (value === false) return 'off'
  if (Array.isArray(value)) return value.length > 0 ? value.join(', ') : 'empty'
  return typeof value === 'string' && value ? value : 'empty'
}

interface StaleChangePromptsProps<F> {
  asks: FieldAsk<F>[]
  labelOf: (field: keyof F & string) => string
  onAnswer: (ask: FieldAsk<F>, usesYours: boolean) => void
}

export function StaleChangePrompts<F>({ asks, labelOf, onAnswer }: StaleChangePromptsProps<F>) {
  return asks.map((ask) => (
    <section key={ask.field} role="alert" aria-label={`${labelOf(ask.field)} needs your choice`} className="flex flex-wrap items-center justify-between gap-x-4 gap-y-2 rounded-lg border bg-muted p-3 text-sm dark:bg-card">
      <p className="min-w-0 flex-1 basis-64 wrap-anywhere">
        {ask.didNotReachTheServer ? 'This did not reach the server, so your choice did not apply.' : 'This changed in another tab or on another device, so your choice did not apply.'} {labelOf(ask.field)} is now {valueText(ask.now)}. You chose {valueText(ask.yours)}.
      </p>
      <div className="flex flex-wrap items-center gap-2">
        <Button variant="outline" onPress={() => onAnswer(ask, true)}>Use my choice</Button>
        <Button variant="ghost" onPress={() => onAnswer(ask, false)}>Keep what is there</Button>
      </div>
    </section>
  ))
}
