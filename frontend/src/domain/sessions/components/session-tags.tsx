import { Cancel01Icon } from '@hugeicons/core-free-icons'
import { HugeiconsIcon } from '@hugeicons/react'
import { useQueryClient } from '@tanstack/react-query'
import { useEffect, useRef, useState, useSyncExternalStore, type FormEvent } from 'react'
import { ToggleButton } from 'react-aria-components'

import type { SessionDto } from '@/api/generated/models/types/session-dto'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { usePeopleDirectory } from '@/domain/people/apis/people'
import { useJournalZone } from '@/domain/settings/apis/user-setting'

import { metadataSaverFor } from '../autosave/metadata-saver'
import { activityNone, knownActivities, knownMoods, tagValuesOf, withTopicAdded, type SessionTagValues } from '../tags'
import { SaveStatusLine } from './save-status'

const chipClass =
  'inline-flex min-h-8 items-center rounded-full border px-3 text-sm outline-none hover:bg-muted data-[focus-visible]:ring-[3px] data-[focus-visible]:ring-ring/50 data-[selected]:border-primary data-[selected]:bg-primary data-[selected]:text-primary-foreground max-md:min-h-11'

export function SessionTags({ session }: { session: SessionDto }) {
  const queryClient = useQueryClient()
  const zone = useJournalZone()
  const saver = metadataSaverFor(session.id, queryClient)
  const status = useSyncExternalStore(saver.subscribe, saver.getStatus)
  const [isEditing, setIsEditing] = useState(false)
  const directory = usePeopleDirectory().data
  const values = saver.newestUnsettledValue() ?? tagValuesOf(session)
  const aiTopics = session.topics.filter((topic) => topic.provenance === 'AiSuggested').map((topic) => topic.name)
  const people = session.personIds.flatMap((id) => directory?.find((person) => person.id === id)?.label ?? [])

  return (
    <section aria-labelledby="entry-tags" className="flex flex-col gap-3">
      <div className="flex items-center justify-between gap-2">
        <h2 id="entry-tags" className="text-lg font-semibold">Tags</h2>
        <Button variant="outline" aria-expanded={isEditing} onPress={() => setIsEditing(!isEditing)}>
          {isEditing ? 'Close tags' : 'Edit tags'}
        </Button>
      </div>
      {isEditing ? (
        <TagEditor values={values} onChange={(next) => saver.edit(next)} />
      ) : (
        <TagSummary values={values} />
      )}
      {aiTopics.length > 0 && <TagLine label="Suggested topics" tags={aiTopics} />}
      {people.length > 0 && <TagLine label="People" tags={people.map((label) => `@${label}`)} />}
      <SaveStatusLine status={status} zone={zone} />
    </section>
  )
}

function TagLine({ label, tags }: { label: string; tags: string[] }) {
  return (
    <div className="flex flex-wrap items-center gap-1.5 text-sm">
      <span className="text-muted-foreground">{label}</span>
      {tags.map((tag) => (
        <Badge key={tag} variant="secondary" className="max-w-full">
          <span dir="auto" className="truncate">{tag}</span>
        </Badge>
      ))}
    </div>
  )
}

function TagSummary({ values }: { values: SessionTagValues }) {
  const activity = values.activity && values.activity !== activityNone ? [values.activity] : []
  if (activity.length + values.moods.length + values.userTopics.length === 0) {
    return <p className="text-sm text-muted-foreground">No tags yet.</p>
  }
  return (
    <div className="flex flex-col gap-1.5">
      {activity.length > 0 && <TagLine label="Activity" tags={activity} />}
      {values.moods.length > 0 && <TagLine label="Moods" tags={values.moods} />}
      {values.userTopics.length > 0 && <TagLine label="Topics" tags={values.userTopics} />}
    </div>
  )
}

interface TagEditorProps {
  values: SessionTagValues
  onChange: (values: SessionTagValues) => void
}

function TagEditor({ values, onChange }: TagEditorProps) {
  const [topic, setTopic] = useState('')
  const topicList = useRef<HTMLUListElement>(null)
  const newTopic = useRef<HTMLInputElement>(null)
  const removedAt = useRef<number | null>(null)

  // The button that had the focus is gone, and with no new target the focus falls to the body of the page.
  useEffect(() => {
    if (removedAt.current === null) return
    const removeButtons = topicList.current?.querySelectorAll('button') ?? []
    const next = removeButtons[Math.min(removedAt.current, removeButtons.length - 1)] ?? newTopic.current
    next?.focus()
    removedAt.current = null
  }, [values.userTopics])

  const removeTopic = (name: string) => {
    removedAt.current = values.userTopics.indexOf(name)
    onChange({ ...values, userTopics: values.userTopics.filter((t) => t !== name) })
  }

  const toggleMood = (mood: string) =>
    onChange({
      ...values,
      moods: values.moods.includes(mood) ? values.moods.filter((m) => m !== mood) : [...values.moods, mood],
    })

  const addTopic = (event: FormEvent) => {
    event.preventDefault()
    onChange({ ...values, userTopics: withTopicAdded(values.userTopics, topic) })
    setTopic('')
  }

  return (
    <div className="flex flex-col gap-4">
      <fieldset className="flex flex-col gap-2">
        <legend className="mb-2 text-sm font-medium">Moods</legend>
        <div className="flex flex-wrap gap-1.5">
          {knownMoods.map((mood) => (
            <ToggleButton key={mood} className={chipClass} isSelected={values.moods.includes(mood)} onChange={() => toggleMood(mood)}>
              {mood}
            </ToggleButton>
          ))}
        </div>
      </fieldset>
      <fieldset className="flex flex-col gap-2">
        <legend className="mb-2 text-sm font-medium">Activity</legend>
        <div className="flex flex-wrap gap-1.5">
          {knownActivities.map((activity) => (
            <ToggleButton
              key={activity}
              className={chipClass}
              isSelected={values.activity === activity}
              onChange={(isSelected) => onChange({ ...values, activity: isSelected ? activity : null })}
            >
              {activity}
            </ToggleButton>
          ))}
        </div>
      </fieldset>
      <fieldset className="flex min-w-0 flex-col gap-2">
        <legend className="mb-2 text-sm font-medium">Topics</legend>
        {values.userTopics.length > 0 && (
          <ul ref={topicList} aria-label="Your topics" className="flex flex-wrap gap-1.5">
            {values.userTopics.map((name) => (
              <li key={name} className="flex max-w-full items-center gap-1 rounded-full border pl-3 text-sm">
                <span dir="auto" className="min-w-0 truncate">{name}</span>
                <Button
                  variant="ghost"
                  size="icon"
                  className="shrink-0 rounded-full max-md:size-11"
                  aria-label={`Remove topic ${name}`}
                  onPress={() => removeTopic(name)}
                >
                  <HugeiconsIcon icon={Cancel01Icon} strokeWidth={2} />
                </Button>
              </li>
            ))}
          </ul>
        )}
        <form onSubmit={addTopic} className="flex gap-2">
          <Input
            ref={newTopic}
            aria-label="New topic"
            placeholder="Add a topic"
            value={topic}
            maxLength={100}
            onChange={(event) => setTopic(event.target.value)}
          />
          <Button type="submit" variant="outline">Add</Button>
        </form>
      </fieldset>
    </div>
  )
}
