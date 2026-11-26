import { zodResolver } from '@hookform/resolvers/zod'
import type { ReactElement } from 'react'
import { Controller, useForm } from 'react-hook-form'

import type { UserDto, UserDtoAllowedActionsEnumKey } from '@/api/generated/models/types/user-dto'
import type { UserRoleDto } from '@/api/generated/models/types/user-role-dto'
import { userRoleDtoSchema } from '@/api/generated/models/zod/user-role-dto'
import { Button } from '@/components/ui/button'
import { Label } from '@/components/ui/label'
import {
  ResponsiveDialog,
  ResponsiveDialogClose,
  ResponsiveDialogDescription,
  ResponsiveDialogFooter,
  ResponsiveDialogHeader,
  ResponsiveDialogTitle,
  ResponsiveDialogTrigger,
} from '@/components/ui/responsive-dialog'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'

import { TemporaryPasswordDialog } from './temporary-password-dialog'
import { isUserRole, ROLE_LABELS, type UserRole } from './user-role'

export type UserAction = UserDtoAllowedActionsEnumKey

interface UserActionsProps {
  user: UserDto
  pendingAction?: UserAction
  /** The user is the caller, so a temporary password keeps the session of this page. Undefined until the current user loads. */
  isSelf?: boolean
  onAddRole: (role: UserRole) => void
  onRemoveRole: (role: UserRole) => void
  onDelete: () => void
}

type ActionControl = (props: UserActionsProps) => ReactElement | null

const CONTROLS: Record<UserAction, ActionControl | null> = {
  Update: null,
  AddRole,
  RemoveRole,
  AddPermission: null,
  RemovePermission: null,
  Delete: ConfirmDelete,
  // Until the current user loads, the dialog cannot tell the caller what happens to their own sessions, so it waits.
  IssueTemporaryPassword: ({ user, isSelf }) => (isSelf === undefined ? null : <TemporaryPasswordDialog user={user} isSelf={isSelf} />),
  // The page shows the links of the user in a section of their own, with a Remove at each.
  ManageExternalLogins: null,
}

export function UserActions(props: UserActionsProps) {
  const { user } = props
  const controls = user.allowedActions.flatMap((action) => {
    const control = CONTROLS[action]
    return control ? [{ action, Control: control }] : []
  })

  if (controls.length === 0) {
    return null
  }

  return (
    <div role="group" aria-label={`Actions for ${user.fullName}`} className="flex flex-wrap gap-2">
      {controls.map(({ action, Control }) => (
        <Control key={action} {...props} />
      ))}
    </div>
  )
}

const buttonState = (action: UserAction, pendingAction: UserAction | undefined) => ({
  isPending: pendingAction === action,
  isDisabled: pendingAction !== undefined && pendingAction !== action,
})

function AddRole(props: UserActionsProps) {
  const choices = props.user.addableRoles
  if (choices.length === 0) return null
  return (
    <RoleDialog
      {...props}
      action="AddRole"
      choices={choices}
      button="Add role"
      title={`Add a role to ${props.user.fullName}`}
      description="The user gets each permission of the role, also one that was removed before. The other roles stay."
      keep="Keep roles"
      onPick={props.onAddRole}
    />
  )
}

function RemoveRole(props: UserActionsProps) {
  if (props.user.roles.length === 0) return null
  return (
    <RoleDialog
      {...props}
      action="RemoveRole"
      choices={props.user.roles}
      button="Remove role"
      title={`Remove a role from ${props.user.fullName}`}
      description="The user keeps a permission that another role or an added permission gives. With no role, the user has only their own journal."
      keep="Keep roles"
      onPick={props.onRemoveRole}
    />
  )
}

interface RoleDialogProps extends UserActionsProps {
  action: 'AddRole' | 'RemoveRole'
  choices: readonly UserRole[]
  button: string
  title: string
  description: string
  keep: string
  onPick: (role: UserRole) => void
}

function RoleDialog({ action, pendingAction, button, ...props }: RoleDialogProps) {
  return (
    <ResponsiveDialogTrigger>
      <Button variant="outline" {...buttonState(action, pendingAction)}>
        {button}
      </Button>
      <ResponsiveDialog>
        <RoleForm button={button} {...props} />
      </ResponsiveDialog>
    </ResponsiveDialogTrigger>
  )
}

function RoleForm({ choices, button, title, description, keep, onPick }: Pick<RoleDialogProps, 'choices' | 'button' | 'title' | 'description' | 'keep' | 'onPick'>) {
  const form = useForm<UserRoleDto>({
    resolver: zodResolver(userRoleDtoSchema),
    defaultValues: { role: choices[0] },
  })
  const handleConfirm = () => void form.handleSubmit(({ role }) => onPick(role))()

  return (
    <>
      <ResponsiveDialogHeader>
        <ResponsiveDialogTitle>{title}</ResponsiveDialogTitle>
        <ResponsiveDialogDescription>{description}</ResponsiveDialogDescription>
      </ResponsiveDialogHeader>
      <div className="pt-3 pb-4">
        <Controller
          control={form.control}
          name="role"
          render={({ field }) => (
            <Select
              value={field.value}
              onChange={(key) => {
                if (isUserRole(key)) field.onChange(key)
              }}
              className="flex w-full flex-col gap-2"
            >
              <Label>Role</Label>
              <SelectTrigger ref={field.ref} className="w-full">
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                {choices.map((role) => (
                  <SelectItem key={role} id={role}>
                    {ROLE_LABELS[role]}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          )}
        />
      </div>
      <ResponsiveDialogFooter>
        <ResponsiveDialogClose>{keep}</ResponsiveDialogClose>
        <ResponsiveDialogClose variant="default" onPress={handleConfirm}>
          {button}
        </ResponsiveDialogClose>
      </ResponsiveDialogFooter>
    </>
  )
}

function ConfirmDelete({ user, pendingAction, onDelete }: UserActionsProps) {
  return (
    <ResponsiveDialogTrigger>
      <Button variant="outline" {...buttonState('Delete', pendingAction)}>
        Delete
      </Button>
      <ResponsiveDialog>
        <ResponsiveDialogHeader>
          <ResponsiveDialogTitle>Delete {user.fullName}?</ResponsiveDialogTitle>
          <ResponsiveDialogDescription>
            The user loses access to Recall. You cannot undo a delete.
          </ResponsiveDialogDescription>
        </ResponsiveDialogHeader>
        <ResponsiveDialogFooter>
          <ResponsiveDialogClose>Keep user</ResponsiveDialogClose>
          <ResponsiveDialogClose variant="destructive" onPress={onDelete}>
            Delete user
          </ResponsiveDialogClose>
        </ResponsiveDialogFooter>
      </ResponsiveDialog>
    </ResponsiveDialogTrigger>
  )
}
