import { useSyncExternalStore } from 'react'

import { Notification } from '@/components/notification'
import { Button } from '@/components/ui/button'
import { Link } from '@/components/ui/link'

import { draftNotices, subscribeToDraftNotices, type DraftNotice } from '../autosave/draft-saver'
import { plainPreviewOf, plainWordsOf } from '../editor/content'

const previewLength = 160

const noticeClass = 'mx-4 mb-4 flex flex-wrap items-center justify-between gap-x-4 gap-y-2 rounded-lg border p-3 text-sm'

async function copyToClipboard(text: string) {
  try {
    await navigator.clipboard.writeText(text)
  } catch {
    // The clipboard API needs a secure context, and Recall can run on plain HTTP on a home network.
    const field = document.createElement('textarea')
    field.value = text
    document.body.append(field)
    field.select()
    const isCopied = document.execCommand('copy')
    field.remove()
    if (!isCopied) throw new Error('The browser refused the copy.')
  }
}

function MovedNotice({ notice }: { notice: Extract<DraftNotice, { kind: 'moved' }> }) {
  return (
    <section role="status" aria-label="Your words moved" className={`${noticeClass} bg-muted dark:bg-card`}>
      <p className="min-w-0 flex-1 basis-64">An entry that you wrote was deleted in another tab or device. Your words are safe in a new entry.</p>
      <div className="flex items-center gap-2">
        {notice.sessionId && (
          <Link to="/sessions/$id" params={{ id: notice.sessionId }} className="font-medium underline underline-offset-4">
            Open the new entry
          </Link>
        )}
        <Button variant="ghost" onPress={() => notice.saver.dismissMovedNotice()}>Dismiss</Button>
      </div>
    </section>
  )
}

function CopyWords({ words }: { words: string }) {
  const copy = () =>
    copyToClipboard(plainWordsOf(words)).then(
      () => Notification.success('Words copied'),
      () => Notification.error('The browser did not copy the words. Open this page in a newer browser and try again.'),
    )
  return <Button variant="outline" onPress={() => void copy()}>Copy words</Button>
}

function WaitingNotice({ notice }: { notice: Extract<DraftNotice, { kind: 'waiting' }> }) {
  return (
    <section role="alert" aria-label="Words not saved yet" className={`${noticeClass} bg-muted dark:bg-card`}>
      <div className="min-w-0 flex-1 basis-64 space-y-1">
        <p className="font-medium wrap-anywhere">These words are not saved yet. Recall keeps trying while this page is open.</p>
        <p dir="auto" className="line-clamp-2 wrap-anywhere text-muted-foreground">{plainPreviewOf(notice.words, previewLength)}</p>
      </div>
      <div className="flex items-center gap-2">
        <CopyWords words={notice.words} />
        <Button variant="ghost" onPress={() => notice.saver.discard()}>Discard</Button>
      </div>
    </section>
  )
}

function OnlyInMemoryNotice({ notice }: { notice: Extract<DraftNotice, { kind: 'onlyInMemory' }> }) {
  return (
    <section role="alert" aria-label="Words only in this page" className={`${noticeClass} border-destructive/20 bg-destructive/10`}>
      <div className="min-w-0 flex-1 basis-64 space-y-1">
        <p className="font-medium text-destructive wrap-anywhere">This browser has no room to keep these words. They are lost if you close or reload this page before they reach the server. {notice.reason}</p>
        <p dir="auto" className="line-clamp-2 wrap-anywhere text-muted-foreground">{plainPreviewOf(notice.words, previewLength)}</p>
      </div>
      <div className="flex items-center gap-2">
        <CopyWords words={notice.words} />
        {notice.reason && <Button variant="ghost" onPress={() => notice.saver.discard()}>Discard</Button>}
      </div>
    </section>
  )
}

function RefusedNotice({ notice }: { notice: Extract<DraftNotice, { kind: 'refused' }> }) {
  return (
    <section role="alert" aria-label="Words not saved" className={`${noticeClass} border-destructive/20 bg-destructive/10`}>
      <div className="min-w-0 flex-1 basis-64 space-y-1">
        <p className="font-medium text-destructive wrap-anywhere">These words are not saved. {notice.reason}</p>
        <p dir="auto" className="line-clamp-2 wrap-anywhere text-muted-foreground">{plainPreviewOf(notice.words, previewLength)}</p>
        <p className="text-muted-foreground">Copy the words, make them shorter, and put them in a new entry. They stay here until you discard them.</p>
      </div>
      <div className="flex items-center gap-2">
        <CopyWords words={notice.words} />
        <Button variant="ghost" onPress={() => notice.saver.discard()}>Discard</Button>
      </div>
    </section>
  )
}

function Notice({ notice }: { notice: DraftNotice }) {
  switch (notice.kind) {
    case 'moved':
      return <MovedNotice notice={notice} />
    case 'refused':
      return <RefusedNotice notice={notice} />
    case 'waiting':
      return <WaitingNotice notice={notice} />
    case 'onlyInMemory':
      return <OnlyInMemoryNotice notice={notice} />
  }
}

export function DraftNotices() {
  const notices = useSyncExternalStore(subscribeToDraftNotices, draftNotices)
  return notices.map((notice, index) => <Notice key={index} notice={notice} />)
}
