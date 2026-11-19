import { useEffect, useState, type RefObject } from 'react'

import type { CorrectionDto } from '@/api/generated/models/types/correction-dto'
import { Notification } from '@/components/notification'
import { QueryError } from '@/components/query-error'
import { SearchBox } from '@/components/search-box'
import { Button } from '@/components/ui/button'
import {
  ResponsiveDialog,
  ResponsiveDialogClose,
  ResponsiveDialogDescription,
  ResponsiveDialogFooter,
  ResponsiveDialogHeader,
  ResponsiveDialogTitle,
  ResponsiveDialogTrigger,
} from '@/components/ui/responsive-dialog'
import { isProblemDetailsError } from '@/lib/problem-details'

import { useCorrectionDirectory, useDeleteCorrection } from '../apis/corrections'
import { CorrectionForm } from './correction-form'
import { isKnownWord, matching, termMaxLength } from './correction-form-schema'

interface CorrectionsListProps {
  search: string
  onSearch: (text: string, searchWhenTyped: string) => void
  headingRef: RefObject<HTMLHeadingElement | null>
}

export function CorrectionsList({ search, onSearch, headingRef }: CorrectionsListProps) {
  return (
    <section aria-labelledby="misheard-words-heading" className="flex flex-col gap-4">
      <div className="flex flex-col gap-2">
        <h2 id="misheard-words-heading" className="text-lg font-semibold">Misheard words</h2>
        <p className="text-sm text-muted-foreground">
          These are the words that dictation gets wrong, and the ways it mishears them. Cleanup writes your word in their
          place.
        </p>
      </div>
      <div className="flex flex-wrap items-end justify-between gap-4">
        <SearchBox
          label="Search misheard words"
          placeholder="Term or mishearing"
          search={search}
          maxLength={termMaxLength}
          onSearch={onSearch}
        />
        <AddCorrection />
      </div>
      <CorrectionRows search={search} headingRef={headingRef} />
    </section>
  )
}

function CorrectionRows({ search, headingRef }: { search: string; headingRef: RefObject<HTMLHeadingElement | null> }) {
  const directory = useCorrectionDirectory()
  // An edit that clears the mishearings takes away the row, its Edit button, and the open dialog. The dialog keeps the
  // focus while it is open, so the heading takes the focus only after the row is gone. If the refetch failed and the
  // person moved on, a later refetch must not take the focus from them.
  const [madeKnownId, setMadeKnownId] = useState<string | null>(null)
  const isMadeKnownRowGone =
    madeKnownId !== null && !directory.data?.some((correction) => correction.id === madeKnownId && !isKnownWord(correction))
  useEffect(() => {
    if (!isMadeKnownRowGone) return
    const focused = document.activeElement
    if (!focused || focused === document.body || !focused.isConnected) headingRef.current?.focus()
    setMadeKnownId(null)
  }, [isMadeKnownRowGone, headingRef])

  if (directory.isPending) {
    return <p role="status" aria-label="Loading your corrections" className="text-muted-foreground">Loading your corrections</p>
  }
  if (directory.isError) {
    return <QueryError title="Your corrections did not load." error={directory.error} onRetry={() => directory.refetch()} />
  }
  const corrections = directory.data.filter((correction) => !isKnownWord(correction))
  if (corrections.length === 0) {
    return <p className="text-muted-foreground">You have no misheard words yet. Add a word that dictation gets wrong.</p>
  }
  const rows = matching(corrections, search)
  if (rows.length === 0) {
    return <p className="text-muted-foreground wrap-anywhere">No misheard word has “{search.trim()}” in its term or its mishearings.</p>
  }

  return (
    <ul aria-label="Your corrections" className="flex flex-col divide-y rounded-lg border">
      {rows.map((correction) => (
        <li key={correction.id} className="flex flex-wrap items-start justify-between gap-x-3 gap-y-2 p-3">
          <div className="flex min-w-0 flex-1 basis-60 flex-col gap-1">
            <span dir="auto" className="font-medium wrap-anywhere">{correction.canonicalTerm}</span>
            {correction.mishearings.length > 0 && (
              <span dir="auto" className="text-sm text-muted-foreground wrap-anywhere">
                Heard as {correction.mishearings.join(', ')}
              </span>
            )}
            {correction.hardReplace && <span className="text-sm text-muted-foreground">Always replaced</span>}
          </div>
          <div className="flex shrink-0 gap-1">
            <EditCorrection correction={correction} onMadeKnown={setMadeKnownId} />
            <DeleteCorrection correction={correction} headingRef={headingRef} />
          </div>
        </li>
      ))}
    </ul>
  )
}

function AddCorrection() {
  const [isOpen, setIsOpen] = useState(false)

  return (
    <ResponsiveDialogTrigger isOpen={isOpen} onOpenChange={setIsOpen}>
      <Button>Add correction</Button>
      <ResponsiveDialog>
        <CorrectionForm onSaved={() => setIsOpen(false)} />
      </ResponsiveDialog>
    </ResponsiveDialogTrigger>
  )
}

function EditCorrection({ correction, onMadeKnown }: { correction: CorrectionDto; onMadeKnown: (id: string) => void }) {
  const [isOpen, setIsOpen] = useState(false)

  const handleSaved = (saved: CorrectionDto) => {
    setIsOpen(false)
    if (isKnownWord(saved)) onMadeKnown(saved.id)
  }

  return (
    <ResponsiveDialogTrigger isOpen={isOpen} onOpenChange={setIsOpen}>
      <Button variant="ghost" aria-label={`Edit ${correction.canonicalTerm}`}>Edit</Button>
      <ResponsiveDialog>
        <CorrectionForm correction={correction} onSaved={handleSaved} />
      </ResponsiveDialog>
    </ResponsiveDialogTrigger>
  )
}

function DeleteCorrection({
  correction,
  headingRef,
}: {
  correction: CorrectionDto
  headingRef: RefObject<HTMLHeadingElement | null>
}) {
  const remove = useDeleteCorrection()

  const handleDelete = () =>
    remove.mutate({ id: correction.id, version: correction.version }, {
      onSuccess: () => {
        Notification.success('Correction deleted')
        headingRef.current?.focus()
      },
      onError: (error) => {
        if (isProblemDetailsError(error) && error.status === 404) headingRef.current?.focus()
      },
    })

  return (
    <ResponsiveDialogTrigger>
      <Button variant="ghost" aria-label={`Delete ${correction.canonicalTerm}`} isPending={remove.isPending}>
        Delete
      </Button>
      <ResponsiveDialog>
        <ResponsiveDialogHeader>
          <ResponsiveDialogTitle>Delete {correction.canonicalTerm}?</ResponsiveDialogTitle>
          <ResponsiveDialogDescription>
            Cleanup stops using this term and its mishearings. You cannot undo a delete.
          </ResponsiveDialogDescription>
        </ResponsiveDialogHeader>
        <ResponsiveDialogFooter>
          <ResponsiveDialogClose>Keep correction</ResponsiveDialogClose>
          <ResponsiveDialogClose variant="destructive" onPress={handleDelete}>
            Delete correction
          </ResponsiveDialogClose>
        </ResponsiveDialogFooter>
      </ResponsiveDialog>
    </ResponsiveDialogTrigger>
  )
}
