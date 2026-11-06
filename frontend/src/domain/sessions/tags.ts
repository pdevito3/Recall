import type { SessionDto } from '@/api/generated/models/types/session-dto'

export const knownMoods = ['Joyful', 'Content', 'Calm', 'Neutral', 'Tired', 'Anxious', 'Sad', 'Angry', 'Excited', 'Grateful']

export const knownActivities = ['Stationary', 'Walking', 'Eating', 'Commuting', 'Exercising', 'Resting']

export const activityNone = 'None'

export interface SessionTagValues {
  activity: string | null
  moods: string[]
  userTopics: string[]
}

export const tagValuesOf = (session: SessionDto): SessionTagValues => ({
  activity: session.activity ?? null,
  moods: session.moods,
  userTopics: session.topics.filter((topic) => topic.provenance === 'UserSet').map((topic) => topic.name),
})

/** Two Topics match with case ignored, as on the Server (SES-21). */
export function withTopicAdded(topics: string[], added: string): string[] {
  const name = added.trim()
  if (!name || topics.some((topic) => topic.toLowerCase() === name.toLowerCase())) return topics
  return [...topics, name]
}
