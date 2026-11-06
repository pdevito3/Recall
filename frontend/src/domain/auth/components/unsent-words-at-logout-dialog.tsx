import {
  ResponsiveDialog,
  ResponsiveDialogClose,
  ResponsiveDialogDescription,
  ResponsiveDialogFooter,
  ResponsiveDialogHeader,
  ResponsiveDialogTitle,
} from '@/components/ui/responsive-dialog'

import { isAnyWordOnlyInMemory } from '@/domain/sessions/autosave/draft-saver'

import { answerAboutUnsentWords, useIsAskingAboutUnsentWords } from '../apis/unsent-words-at-logout'

export function UnsentWordsAtLogoutDialog() {
  const isOpen = useIsAskingAboutUnsentWords()
  const canKeep = !isOpen || !isAnyWordOnlyInMemory()
  return (
    <ResponsiveDialog role="alertdialog" isOpen={isOpen} isDismissable={false} isKeyboardDismissDisabled showCloseButton={false}>
      <ResponsiveDialogHeader>
        <ResponsiveDialogTitle>Some of your words did not reach the server</ResponsiveDialogTitle>
        <ResponsiveDialogDescription>
          {canKeep
            ? 'You can keep them on this device. They go to your journal when you sign in here again. Anyone who uses this device can read them with the browser tools. If you discard them, you cannot get them back.'
            : 'This browser has no room to keep some of them, so a sign-out loses them. Stay signed in to copy them from the notice on this page. If you discard them, you cannot get them back.'}
        </ResponsiveDialogDescription>
      </ResponsiveDialogHeader>
      <ResponsiveDialogFooter>
        <ResponsiveDialogClose variant="destructive" onPress={() => answerAboutUnsentWords('discard')}>
          Discard words and sign out
        </ResponsiveDialogClose>
        <ResponsiveDialogClose onPress={() => answerAboutUnsentWords(canKeep ? 'keep' : 'stay')}>
          {canKeep ? 'Keep words and sign out' : 'Stay signed in'}
        </ResponsiveDialogClose>
      </ResponsiveDialogFooter>
    </ResponsiveDialog>
  )
}
