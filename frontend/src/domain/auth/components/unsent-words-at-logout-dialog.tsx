import {
  ResponsiveDialog,
  ResponsiveDialogClose,
  ResponsiveDialogDescription,
  ResponsiveDialogFooter,
  ResponsiveDialogHeader,
  ResponsiveDialogTitle,
} from '@/components/ui/responsive-dialog'

import { isAnyWordOnlyInMemory } from '@/domain/sessions/autosave/draft-saver'
import { isAnyTagChangeUnsaved } from '@/domain/sessions/autosave/tag-savers'

import { answerAboutUnsentWords, useIsAskingAboutUnsentWords, useIsAskingOnlyAboutTags } from '../apis/unsent-words-at-logout'

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

export function UnsentWordsAtLogoutDialog() {
  const isOpen = useIsAskingAboutUnsentWords()
  const isOnlyTags = useIsAskingOnlyAboutTags()
  if (isOnlyTags) return <UnsavedTagsDialog />
  const canKeep = !isOpen || !isAnyWordOnlyInMemory()
  const isATagUnsavedToo = isOpen && isAnyTagChangeUnsaved()
  return (
    <ResponsiveDialog role="alertdialog" isOpen={isOpen} isDismissable={false} isKeyboardDismissDisabled showCloseButton={false}>
      <ResponsiveDialogHeader>
        <ResponsiveDialogTitle>Some of your words did not reach the server</ResponsiveDialogTitle>
        <ResponsiveDialogDescription>
          {canKeep
            ? 'You can keep them on this device. They go to your journal when you sign in here again. Anyone who uses this device can read them with the browser tools. If you discard them, you cannot get them back.'
            : 'This browser has no room to keep some of them, so a sign-out loses them. Stay signed in to copy them from the notice on this page. If you discard them, you cannot get them back.'}
          {isATagUnsavedToo && ' A change of your tags also did not reach the server, and a sign-out loses it.'}
        </ResponsiveDialogDescription>
      </ResponsiveDialogHeader>
      <ResponsiveDialogFooter className={canKeep && isATagUnsavedToo ? 'sm:flex-col-reverse' : undefined}>
        <ResponsiveDialogClose variant="destructive" onPress={() => answerAboutUnsentWords('discard')}>
          Discard words and sign out
        </ResponsiveDialogClose>
        <ResponsiveDialogClose onPress={() => answerAboutUnsentWords(canKeep ? 'keep' : 'stay')}>
          {canKeep ? 'Keep words and sign out' : 'Stay signed in'}
        </ResponsiveDialogClose>
        {canKeep && isATagUnsavedToo && <ResponsiveDialogClose onPress={() => answerAboutUnsentWords('stay')}>Stay signed in</ResponsiveDialogClose>}
      </ResponsiveDialogFooter>
    </ResponsiveDialog>
  )
}
