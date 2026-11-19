import { Cancel01Icon } from '@hugeicons/core-free-icons'
import { HugeiconsIcon } from '@hugeicons/react'
import { useRef, useState, type KeyboardEvent, type MouseEvent } from 'react'
import { Input as InputPrimitive } from 'react-aria-components'

import type { CorrectionDto } from '@/api/generated/models/types/correction-dto'
import { Button } from '@/components/ui/button'
import { refusalOf } from '@/hooks/use-server-errors'
import { isProblemDetailsError } from '@/lib/problem-details'
import { cn } from '@/lib/utils'

import { useAddCorrection, useCorrectionDirectory, useDeleteCorrection } from '../apis/corrections'
import { isKnownWord, withTerm } from './correction-form-schema'

function inUseMessage(existing: CorrectionDto): string {
  return isKnownWord(existing)
    ? `“${existing.canonicalTerm}” is already a known word.`
    : `“${existing.canonicalTerm}” is already in your corrections below. Edit it there.`
}

const notSavedMessage = 'The word was not saved. Try again.'

function refusalMessage(error: unknown): string {
  const refusal = refusalOf(error, ['canonicalTerm'])
  const other = refusal.problem?.kind === 'refused' ? refusal.problem.messages : []
  return [refusal.byField.canonicalTerm, ...other].filter(Boolean).join(' ') || notSavedMessage
}

export function KnownWords() {
  const directory = useCorrectionDirectory()
  const add = useAddCorrection()
  const [text, setText] = useState('')
  const [message, setMessage] = useState<string | null>(null)
  const [announcement, setAnnouncement] = useState('')
  const inputRef = useRef<HTMLInputElement>(null)
  const isSending = useRef(false)
  const words = directory.data?.filter(isKnownWord) ?? []

  const handleAdd = () => {
    const term = text.trim()
    if (isSending.current || !directory.data || !term) return
    const existing = withTerm(directory.data, term)
    if (existing) {
      setMessage(inUseMessage(existing))
      return
    }
    isSending.current = true
    setMessage(null)
    add.mutate(
      { canonicalTerm: term, mishearings: [], hardReplace: false },
      {
        onSuccess: (saved) => {
          setText('')
          setAnnouncement(`Added ${saved.canonicalTerm}.`)
        },
        onError: (error) => setMessage(refusalMessage(error)),
        onSettled: () => {
          isSending.current = false
        },
      },
    )
  }

  const handleKeyDown = (event: KeyboardEvent<HTMLInputElement>) => {
    if (event.key !== 'Enter') return
    event.preventDefault()
    handleAdd()
  }

  // A press on the free space of the field, not on a pill, puts the caret in the input, as in a text field.
  const handleFieldClick = (event: MouseEvent<HTMLDivElement>) => {
    if (event.target === event.currentTarget || event.target instanceof HTMLUListElement) inputRef.current?.focus()
  }

  const handleRemoved = (word: CorrectionDto) => {
    setAnnouncement(`Removed ${word.canonicalTerm}.`)
    inputRef.current?.focus()
  }

  return (
    <section aria-labelledby="known-words-heading" className="flex flex-col gap-2">
      <h2 id="known-words-heading" className="text-lg font-semibold">Known words</h2>
      <p id="known-words-hint" className="text-sm text-muted-foreground">
        Cleanup and cloud dictation use these words to spell names and terms.
      </p>
      <div
        data-slot="known-words-field"
        onClick={handleFieldClick}
        className="flex min-h-8 cursor-text flex-wrap items-center gap-1 max-md:gap-2 rounded-lg border border-input bg-transparent p-1 text-sm transition-colors focus-within:border-ring focus-within:ring-3 focus-within:ring-ring/50 has-aria-invalid:border-destructive has-aria-invalid:ring-3 has-aria-invalid:ring-destructive/20 max-md:min-h-11 dark:bg-input/30 dark:has-aria-invalid:border-destructive/50 dark:has-aria-invalid:ring-destructive/40"
      >
        {words.length > 0 && (
          <ul aria-label="Your known words" className="contents">
            {words.map((word) => (
              <KnownWordPill key={word.id} word={word} onRemoved={handleRemoved} />
            ))}
          </ul>
        )}
        <InputPrimitive
          ref={inputRef}
          aria-labelledby="known-words-heading"
          aria-describedby={message ? 'known-words-hint known-words-message' : 'known-words-hint'}
          aria-invalid={Boolean(message)}
          dir="auto"
          autoComplete="off"
          spellCheck={false}
          enterKeyHint="done"
          placeholder="Add a word and press Enter"
          value={text}
          readOnly={add.isPending}
          disabled={!directory.data}
          onChange={(event) => {
            setText(event.target.value)
            setMessage(null)
          }}
          onKeyDown={handleKeyDown}
          className="h-6 min-w-[min(15rem,100%)] flex-1 bg-transparent px-2 text-base outline-none placeholder:text-muted-foreground read-only:text-muted-foreground disabled:cursor-not-allowed max-md:h-9 md:min-w-52 md:text-sm"
        />
      </div>
      {message && (
        <p id="known-words-message" role="alert" className="text-sm text-destructive wrap-anywhere">
          {message}
        </p>
      )}
      <p role="status" className="sr-only">
        {announcement}
      </p>
    </section>
  )
}

interface KnownWordPillProps {
  word: CorrectionDto
  onRemoved: (word: CorrectionDto) => void
}

function KnownWordPill({ word, onRemoved }: KnownWordPillProps) {
  const remove = useDeleteCorrection()

  const handleRemove = () =>
    remove.mutate({ id: word.id, version: word.version }, {
      onSuccess: () => onRemoved(word),
      // Another device removed the word first, so its pill goes away too.
      onError: (error) => {
        if (isProblemDetailsError(error) && error.status === 404) onRemoved(word)
      },
    })

  return (
    <li
      dir="auto"
      className={cn(
        'flex min-h-6 min-w-0 items-center gap-0.5 rounded-sm bg-muted pl-2 text-sm font-medium wrap-anywhere text-foreground max-md:min-h-8',
        remove.isPending && 'opacity-50',
      )}
    >
      {word.canonicalTerm}
      <Button
        variant="ghost"
        size="icon-xs"
        aria-label={`Remove ${word.canonicalTerm}`}
        isPending={remove.isPending}
        onPress={handleRemove}
        className="shrink-0 opacity-60 hover:opacity-100 max-md:size-8 max-md:min-h-0 max-md:min-w-0 max-md:tap-area"
      >
        <HugeiconsIcon icon={Cancel01Icon} strokeWidth={2} />
      </Button>
    </li>
  )
}
