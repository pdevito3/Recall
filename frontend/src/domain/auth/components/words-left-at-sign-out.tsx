import { Button } from '@/components/ui/button'
import { CopyWords } from '@/domain/sessions/components/draft-notices'
import { plainPreviewOf } from '@/domain/sessions/editor/content'

import type { LeftTheApp } from '../apis/leave-the-app'
import { audioText } from './audio-text'
import { AudioThatDidNotGo } from './audio-that-did-not-go'

const previewLength = 200

const titles = {
  signedOut: 'You signed out',
  signedOutInAnotherTab: 'You signed out in another tab',
  sessionEnded: 'Your session ended',
  sessionUnchecked: 'We could not check your session',
  pageDidNotLoad: 'This page did not load',
}

export function WordsLeftAtSignOut({ why, words, audio, leave }: Exclude<LeftTheApp, { why: 'anotherPerson' }>) {
  return (
    <main className="flex min-h-svh items-center justify-center p-4">
      <section role="alert" aria-labelledby="words-left-title" className="flex w-full max-w-md flex-col gap-3 rounded-lg border bg-card p-4">
        <h1 id="words-left-title" className="text-lg font-semibold">{titles[why]}</h1>
        {words.length > 0 && (
          <>
            <p className="text-sm text-muted-foreground">
              {words.length === 1
                ? 'Some words of this entry did not reach the server, and this browser has no room to keep them. They go when you leave this page, so copy the entry first.'
                : 'Some words of these entries did not reach the server, and this browser has no room to keep them. They go when you leave this page, so copy each entry first.'}
            </p>
            <ul aria-label="Entries with words that only this page holds" className="max-h-80 space-y-2 overflow-y-auto rounded-lg border p-3 text-sm">
              {words.map((text, index) => (
                <li key={index} className="space-y-1.5">
                  <p dir="auto" className="line-clamp-3 wrap-anywhere">{plainPreviewOf(text, previewLength)}</p>
                  <CopyWords words={text} />
                </li>
              ))}
            </ul>
          </>
        )}
        {audio.length > 0 && (
          <>
            <p className="text-sm text-muted-foreground">{audioText(audio, 'It')}</p>
            <AudioThatDidNotGo audio={audio} />
          </>
        )}
        <Button variant={words.length > 0 ? 'destructive' : undefined} className="self-end" onPress={leave}>Leave this page</Button>
      </section>
    </main>
  )
}
