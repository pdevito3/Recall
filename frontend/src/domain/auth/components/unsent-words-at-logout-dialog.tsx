import {
  ResponsiveDialog,
  ResponsiveDialogClose,
  ResponsiveDialogDescription,
  ResponsiveDialogFooter,
  ResponsiveDialogHeader,
  ResponsiveDialogTitle,
} from '@/components/ui/responsive-dialog'

import type { StoredCapture } from '@/domain/capture/audio-store'
import { clock } from '@/domain/capture/audio-time'
import { unsentMsOf } from '@/domain/capture/upload-plan'
import type { UnsentWords } from '@/domain/sessions/autosave/draft-saver'
import { CopyWords } from '@/domain/sessions/components/draft-notices'
import { plainPreviewOf } from '@/domain/sessions/editor/content'

import { answerAboutUnsentWords, useQuestionAboutUnsentWords } from '../apis/unsent-words-at-logout'

const previewLength = 200

function UnsavedTagsDialog() {
  return (
    <ResponsiveDialog role="alertdialog" isOpen isDismissable={false} isKeyboardDismissDisabled showCloseButton={false}>
      <ResponsiveDialogHeader>
        <ResponsiveDialogTitle>A change of your tags did not reach the server</ResponsiveDialogTitle>
        <ResponsiveDialogDescription>A sign-out loses this change. Stay signed in to keep it on the entry page.</ResponsiveDialogDescription>
      </ResponsiveDialogHeader>
      <ResponsiveDialogFooter>
        <ResponsiveDialogClose variant="destructive" onPress={() => answerAboutUnsentWords('keep')}>
          Sign out without this change
        </ResponsiveDialogClose>
        <ResponsiveDialogClose onPress={() => answerAboutUnsentWords('stay')}>Stay signed in</ResponsiveDialogClose>
      </ResponsiveDialogFooter>
    </ResponsiveDialog>
  )
}

function keepingText(unsent: UnsentWords[]) {
  const refused = unsent.filter(({ isRefused }) => isRefused).length
  if (refused === 0) return 'You can keep them on this device. They go to your journal when you sign in here again. Anyone who uses this device can read them with the browser tools.'
  const refusedText = 'The server refused the words marked below, so they cannot go to your journal as they are. If you keep them, they stay on this device, and a notice shows them when you sign in here again, so that you can copy them. Anyone who uses this device can read them with the browser tools.'
  return refused === unsent.length ? refusedText : `You can keep them on this device. Other words go to your journal when you sign in here again. ${refusedText}`
}

function WordsThatDidNotGo({ unsent }: { unsent: UnsentWords[] }) {
  return (
    <ul aria-label="Words that did not reach the server" className="mt-3 max-h-48 space-y-2 overflow-y-auto rounded-lg border p-3 text-sm">
      {unsent.map(({ words, isRefused, isOnlyInMemory }, index) => (
        <li key={index} className="space-y-1.5">
          <p dir="auto" className="line-clamp-2 wrap-anywhere">{plainPreviewOf(words, previewLength)}</p>
          <CopyWords words={words} />
          {isRefused && <p className="text-destructive">The server refused these words.</p>}
          {isOnlyInMemory && <p className="text-destructive">Only an open page holds these words.</p>}
        </li>
      ))}
    </ul>
  )
}

function audioText(audio: StoredCapture[]) {
  const total = clock(audio.reduce((sum, capture) => sum + unsentMsOf(capture), 0))
  const refused = audio.some(({ problem }) => problem?.kind === 'refused') ? ' A recording that the server refused waits on the journal page for you to send it again.' : ''
  return `${total} of audio did not reach the server. If you keep it, it stays in this browser and uploads when you sign in here again.${refused}`
}

/** The question names what did not go: words, audio, or both. */
function namesOf(hasWords: boolean, hasAudio: boolean) {
  if (!hasAudio) return { title: 'Some of your words did not reach the server', thing: 'words', discarded: ' If you discard them, these words go from this device, and you cannot get them back.' }
  const tryWords = ' Recall first tries to make words of the part of the audio that the server has.'
  if (!hasWords) return { title: 'Some of your audio did not reach the server', thing: 'audio', discarded: ` If you discard it, the audio goes from this browser, and you cannot get it back.${tryWords}` }
  return { title: 'Some of your words and audio did not reach the server', thing: 'all', discarded: ` If you discard them, these words and this audio go from this device, and you cannot get them back.${tryWords}` }
}

function AudioThatDidNotGo({ audio }: { audio: StoredCapture[] }) {
  return (
    <ul aria-label="Audio that did not reach the server" className="mt-3 space-y-2 rounded-lg border p-3 text-sm">
      {audio.map((capture) => (
        <li key={capture.clientRecordingId}>
          A recording of {clock(capture.durationMs)}. The server lacks {clock(unsentMsOf(capture))} of it.
          {capture.problem?.kind === 'refused' && <p className="text-destructive">The server refused this audio.</p>}
        </li>
      ))}
    </ul>
  )
}

export function UnsentWordsAtLogoutDialog() {
  const question = useQuestionAboutUnsentWords()
  const audio = question?.audio ?? []
  if (question?.unsent.length === 0 && audio.length === 0) return <UnsavedTagsDialog />
  const unsent = question?.unsent ?? []
  const hasWords = unsent.length > 0
  const canKeep = !unsent.some(({ isOnlyInMemory }) => isOnlyInMemory)
  const isATagUnsavedToo = question?.isATagUnsaved ?? false
  const names = namesOf(hasWords, audio.length > 0)
  return (
    <ResponsiveDialog role="alertdialog" isOpen={question !== null} isDismissable={false} isKeyboardDismissDisabled showCloseButton={false}>
      <ResponsiveDialogHeader>
        <ResponsiveDialogTitle>{names.title}</ResponsiveDialogTitle>
        <ResponsiveDialogDescription>
          {hasWords && (canKeep ? keepingText(unsent) : 'This browser has no room to keep some of them, so a sign-out loses them. Stay signed in to copy them from the notice on the page that holds them.')}
          {audio.length > 0 && `${hasWords ? ' ' : ''}${audioText(audio)}`}
          {names.discarded}
          {isATagUnsavedToo && ' A change of your tags also did not reach the server, and a sign-out loses it.'}
        </ResponsiveDialogDescription>
      </ResponsiveDialogHeader>
      {hasWords && <WordsThatDidNotGo unsent={unsent} />}
      {audio.length > 0 && <AudioThatDidNotGo audio={audio} />}
      <ResponsiveDialogFooter className={canKeep && isATagUnsavedToo ? 'sm:flex-col-reverse' : undefined}>
        <ResponsiveDialogClose variant="destructive" onPress={() => answerAboutUnsentWords('discard')}>
          Discard {names.thing} and sign out
        </ResponsiveDialogClose>
        <ResponsiveDialogClose onPress={() => answerAboutUnsentWords(canKeep ? 'keep' : 'stay')}>
          {canKeep ? `Keep ${names.thing} and sign out` : 'Stay signed in'}
        </ResponsiveDialogClose>
        {canKeep && isATagUnsavedToo && <ResponsiveDialogClose onPress={() => answerAboutUnsentWords('stay')}>Stay signed in</ResponsiveDialogClose>}
      </ResponsiveDialogFooter>
    </ResponsiveDialog>
  )
}
