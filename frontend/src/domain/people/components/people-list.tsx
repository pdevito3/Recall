import { useState, type FormEvent, type RefObject } from 'react'

import type { PersonDto } from '@/api/generated/models/types/person-dto'
import { Notification } from '@/components/notification'
import { QueryError } from '@/components/query-error'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import {
  ResponsiveDialog,
  ResponsiveDialogClose,
  ResponsiveDialogDescription,
  ResponsiveDialogFooter,
  ResponsiveDialogHeader,
  ResponsiveDialogTitle,
  ResponsiveDialogTrigger,
} from '@/components/ui/responsive-dialog'
import { useAddPerson, useDeletePerson, usePeopleDirectory, useRenamePerson } from '@/domain/people/apis/people'
import { isProblemDetailsError } from '@/lib/problem-details'

import { PersonDetailsForm } from './person-details-form'

const longestName = 200

function FailedSave({ error }: { error: Error | null }) {
  if (!error) return null
  return <p role="alert" className="text-sm text-destructive wrap-anywhere">{error.message}</p>
}

export function PeopleList({ headingRef }: { headingRef: RefObject<HTMLHeadingElement | null> }) {
  const directory = usePeopleDirectory()

  return (
    <div className="flex flex-col gap-6">
      <p className="max-w-prose text-muted-foreground">
        These are the people you tag in your entries. Type @ in an entry to tag a person.
      </p>
      <AddPersonForm />
      {directory.isPending && <p role="status" className="text-muted-foreground">Loading your people</p>}
      {directory.isError && (
        <QueryError title="Your people did not load." error={directory.error} onRetry={() => directory.refetch()} />
      )}
      {directory.data?.length === 0 && <p className="text-muted-foreground">You have no people yet.</p>}
      {directory.data && directory.data.length > 0 && (
        <ul aria-label="Your people" className="flex flex-col divide-y rounded-lg border">
          {directory.data.map((person) => (
            <li key={person.id} className="flex flex-col gap-1 p-2 pl-3">
              <div className="flex items-center justify-between gap-3">
                <span dir="auto" className="min-w-0 wrap-anywhere">{person.label}</span>
                <div className="flex shrink-0 gap-1">
                  <DescribePerson person={person} />
                  <RenamePerson person={person} />
                  <DeletePerson person={person} headingRef={headingRef} />
                </div>
              </div>
              {person.fullName && (
                <span dir="auto" className="pr-1 text-sm text-muted-foreground wrap-anywhere">{person.fullName}</span>
              )}
              {person.notes && (
                <span dir="auto" className="line-clamp-3 pr-1 pb-1 text-sm whitespace-pre-line text-muted-foreground wrap-anywhere">
                  {person.notes}
                </span>
              )}
            </li>
          ))}
        </ul>
      )}
    </div>
  )
}

function AddPersonForm() {
  const add = useAddPerson()
  const [label, setLabel] = useState('')

  const handleSubmit = (event: FormEvent) => {
    event.preventDefault()
    if (!label.trim()) return
    add.mutate(label.trim(), { onSuccess: () => setLabel('') })
  }

  return (
    <form onSubmit={handleSubmit} className="flex flex-col gap-2">
      <Label htmlFor="new-person">Add a person</Label>
      <div className="flex gap-2">
        <Input
          id="new-person"
          value={label}
          maxLength={longestName}
          onChange={(event) => setLabel(event.target.value)}
        />
        <Button type="submit" variant="outline" isPending={add.isPending}>Add</Button>
      </div>
      <FailedSave error={add.error} />
    </form>
  )
}

function DescribePerson({ person }: { person: PersonDto }) {
  const [isOpen, setIsOpen] = useState(false)

  return (
    <ResponsiveDialogTrigger isOpen={isOpen} onOpenChange={setIsOpen}>
      <Button variant="ghost" aria-label={`Edit the details of ${person.label}`}>Details</Button>
      <ResponsiveDialog>
        <PersonDetailsForm person={person} onSaved={() => setIsOpen(false)} />
      </ResponsiveDialog>
    </ResponsiveDialogTrigger>
  )
}

function RenamePerson({ person }: { person: PersonDto }) {
  const [isOpen, setIsOpen] = useState(false)

  return (
    <ResponsiveDialogTrigger isOpen={isOpen} onOpenChange={setIsOpen}>
      <Button variant="ghost" aria-label={`Rename ${person.label}`}>Rename</Button>
      <ResponsiveDialog>
        <RenameForm id={person.id} initialLabel={person.label} onRenamed={() => setIsOpen(false)} />
      </ResponsiveDialog>
    </ResponsiveDialogTrigger>
  )
}

function DeletePerson({ person, headingRef }: { person: PersonDto; headingRef: RefObject<HTMLHeadingElement | null> }) {
  const remove = useDeletePerson()

  const handleDelete = () =>
    remove.mutate({ id: person.id, version: person.version }, {
      onSuccess: () => {
        Notification.success('Person deleted')
        headingRef.current?.focus()
      },
      onError: (error) => {
        if (isProblemDetailsError(error) && error.status === 404) headingRef.current?.focus()
      },
    })

  return (
    <ResponsiveDialogTrigger>
      <Button variant="ghost" aria-label={`Delete ${person.label}`} isPending={remove.isPending}>
        Delete
      </Button>
      <ResponsiveDialog>
        <ResponsiveDialogHeader>
          <ResponsiveDialogTitle>Delete {person.label}?</ResponsiveDialogTitle>
          <ResponsiveDialogDescription>
            Each entry that tags this person keeps the name as plain words, and its earlier versions keep the tag. You
            cannot undo a delete.
          </ResponsiveDialogDescription>
        </ResponsiveDialogHeader>
        <ResponsiveDialogFooter>
          <ResponsiveDialogClose>Keep person</ResponsiveDialogClose>
          <ResponsiveDialogClose variant="destructive" onPress={handleDelete}>
            Delete person
          </ResponsiveDialogClose>
        </ResponsiveDialogFooter>
      </ResponsiveDialog>
    </ResponsiveDialogTrigger>
  )
}

function RenameForm({ id, initialLabel, onRenamed }: { id: string; initialLabel: string; onRenamed: () => void }) {
  const rename = useRenamePerson()
  const [label, setLabel] = useState(initialLabel)

  const handleSubmit = (event: FormEvent) => {
    event.preventDefault()
    if (!label.trim()) return
    rename.mutate({ id, label: label.trim() }, { onSuccess: onRenamed })
  }

  return (
    <form onSubmit={handleSubmit} className="flex flex-col gap-4">
      <ResponsiveDialogHeader>
        <ResponsiveDialogTitle>Rename {initialLabel}</ResponsiveDialogTitle>
        <ResponsiveDialogDescription>
          Each entry that tags this person shows the new name, and keeps an earlier version with the old name.
        </ResponsiveDialogDescription>
      </ResponsiveDialogHeader>
      <div className="flex flex-col gap-2">
        <Label htmlFor={`rename-${id}`}>Name</Label>
        <Input
          id={`rename-${id}`}
          value={label}
          maxLength={longestName}
          onChange={(event) => setLabel(event.target.value)}
        />
        <FailedSave error={rename.error} />
      </div>
      <ResponsiveDialogFooter>
        <ResponsiveDialogClose>Cancel</ResponsiveDialogClose>
        <Button type="submit" isPending={rename.isPending}>Save name</Button>
      </ResponsiveDialogFooter>
    </form>
  )
}
