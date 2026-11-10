import { Button } from '@/components/ui/button'
import { CopyWords } from '@/domain/sessions/components/draft-notices'
import { plainPreviewOf } from '@/domain/sessions/editor/content'

import type { WordsLeftAtSignOut as Left } from '../apis/words-left-at-sign-out'

const previewLength = 200

export function WordsLeftAtSignOut({ words, leave }: Left) {
  return (
    <main className="flex min-h-svh items-center justify-center p-4">
      <section role="alert" aria-labelledby="words-left-title" className="flex w-full max-w-md flex-col gap-3 rounded-lg border bg-card p-4">
        <h1 id="words-left-title" className="text-lg font-semibold">You signed out in another tab</h1>
        <p className="text-sm text-muted-foreground">These words did not reach the server, and this browser has no room to keep them. They go when you leave this page, so copy them first.</p>
        <ul aria-label="Words that only this page holds" className="max-h-80 space-y-2 overflow-y-auto rounded-lg border p-3 text-sm">
          {words.map((text, index) => (
            <li key={index} className="space-y-1.5">
              <p dir="auto" className="line-clamp-3 wrap-anywhere">{plainPreviewOf(text, previewLength)}</p>
              <CopyWords words={text} />
            </li>
          ))}
        </ul>
        <Button variant="destructive" className="self-end" onPress={leave}>Leave this page</Button>
      </section>
    </main>
  )
}
