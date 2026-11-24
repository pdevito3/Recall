import { Button } from '@/components/ui/button'
import {
  ResponsiveDialog,
  ResponsiveDialogClose,
  ResponsiveDialogDescription,
  ResponsiveDialogFooter,
  ResponsiveDialogHeader,
  ResponsiveDialogTitle,
} from '@/components/ui/responsive-dialog'
import type { Leaving } from '@/hooks/use-shown-once'

interface LeaveQuestionProps {
  leaving: Leaving
  title: string
  description: string
}

export function LeaveQuestion({ leaving, title, description }: LeaveQuestionProps) {
  return (
    <ResponsiveDialog
      role="alertdialog"
      isOpen={leaving.isAsking}
      onOpenChange={(isOpen) => {
        if (!isOpen) leaving.onStay()
      }}
      isDismissable={false}
      showCloseButton={false}
    >
      <ResponsiveDialogHeader>
        <ResponsiveDialogTitle>{title}</ResponsiveDialogTitle>
        <ResponsiveDialogDescription>{description}</ResponsiveDialogDescription>
      </ResponsiveDialogHeader>
      <ResponsiveDialogFooter>
        <Button variant="outline" onPress={leaving.onLeave}>
          Leave
        </Button>
        <ResponsiveDialogClose variant="default">Stay</ResponsiveDialogClose>
      </ResponsiveDialogFooter>
    </ResponsiveDialog>
  )
}
