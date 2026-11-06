import { useQueryClient } from '@tanstack/react-query'
import { useNavigate } from '@tanstack/react-router'

import type { SessionDto } from '@/api/generated/models/types/session-dto'
import { Notification } from '@/components/notification'
import { Button } from '@/components/ui/button'
import {
  ResponsiveDialog,
  ResponsiveDialogClose,
  ResponsiveDialogDescription,
  ResponsiveDialogFooter,
  ResponsiveDialogHeader,
  ResponsiveDialogTitle,
  ResponsiveDialogTrigger,
} from '@/components/ui/responsive-dialog'

import { useDeleteSession } from '../apis/delete-session'
import { SessionKeys } from '../apis/session.keys'
import { removeTimelineRow } from '../apis/timeline-cache'
import { discardDraftSavers } from '../autosave/draft-saver'
import { discardMetadataSaver } from '../autosave/metadata-saver'

export function DeleteSession({ session }: { session: SessionDto }) {
  const remove = useDeleteSession()
  const queryClient = useQueryClient()
  const navigate = useNavigate()

  const handleDelete = () =>
    remove.mutate(session.id, {
      onSuccess: async () => {
        discardDraftSavers(session.clientSessionId)
        discardMetadataSaver(session.id)
        removeTimelineRow(queryClient, session.id)
        Notification.success('Entry deleted')
        // The page leaves first: a query on the screen reads a dropped key again, and gets a 404.
        await navigate({ to: '/' })
        queryClient.removeQueries({ queryKey: SessionKeys.detail(session.id) })
      },
    })

  return (
    <ResponsiveDialogTrigger>
      <Button variant="outline" isPending={remove.isPending}>Delete</Button>
      <ResponsiveDialog role="alertdialog">
        <ResponsiveDialogHeader>
          <ResponsiveDialogTitle>Delete this entry?</ResponsiveDialogTitle>
          <ResponsiveDialogDescription>
            The words, the tags, and each earlier version go away. You cannot undo a delete.
          </ResponsiveDialogDescription>
        </ResponsiveDialogHeader>
        <ResponsiveDialogFooter>
          <ResponsiveDialogClose>Keep entry</ResponsiveDialogClose>
          <ResponsiveDialogClose variant="destructive" onPress={handleDelete}>
            Delete entry
          </ResponsiveDialogClose>
        </ResponsiveDialogFooter>
      </ResponsiveDialog>
    </ResponsiveDialogTrigger>
  )
}
