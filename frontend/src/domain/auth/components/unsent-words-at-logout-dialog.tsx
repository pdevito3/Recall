import {
  ResponsiveDialog,
  ResponsiveDialogClose,
  ResponsiveDialogDescription,
  ResponsiveDialogFooter,
  ResponsiveDialogHeader,
  ResponsiveDialogTitle,
} from '@/components/ui/responsive-dialog'

import { answerAboutUnsentWords, useIsAskingAboutUnsentWords } from '../apis/unsent-words-at-logout'

export function UnsentWordsAtLogoutDialog() {
  const isOpen = useIsAskingAboutUnsentWords()
  return (
    <ResponsiveDialog role="alertdialog" isOpen={isOpen} isDismissable={false} isKeyboardDismissDisabled showCloseButton={false}>
      <ResponsiveDialogHeader>
        <ResponsiveDialogTitle>Some of your words did not reach the server</ResponsiveDialogTitle>
        <ResponsiveDialogDescription>
          You can keep them on this device. They go to your journal when you sign in here again. Anyone who uses this device can read
          them with the browser tools. If you discard them, you cannot get them back.
        </ResponsiveDialogDescription>
      </ResponsiveDialogHeader>
      <ResponsiveDialogFooter>
        <ResponsiveDialogClose variant="destructive" onPress={() => answerAboutUnsentWords('discard')}>
          Discard words and sign out
        </ResponsiveDialogClose>
        <ResponsiveDialogClose onPress={() => answerAboutUnsentWords('keep')}>
          Keep words and sign out
        </ResponsiveDialogClose>
      </ResponsiveDialogFooter>
    </ResponsiveDialog>
  )
}
