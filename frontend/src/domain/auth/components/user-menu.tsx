import { Logout01Icon, UserCircleIcon } from '@hugeicons/core-free-icons'
import { HugeiconsIcon } from '@hugeicons/react'

import {
  DropdownMenu,
  DropdownMenuGroup,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu'
import { SidebarMenuButton } from '@/components/ui/sidebar'

import { useLogout } from '../apis/auth-actions'
import { useAuth } from '../apis/get-user'

export function UserMenu() {
  const { username } = useAuth()
  const logout = useLogout()
  const name = username ?? 'Your account'

  return (
    <DropdownMenuTrigger>
      <SidebarMenuButton tooltip={name} aria-label={`User menu for ${name}`}>
        <HugeiconsIcon icon={UserCircleIcon} />
        <span className="truncate">{name}</span>
      </SidebarMenuButton>
      <DropdownMenu aria-label="User menu" placement="top start" className="w-56">
        <DropdownMenuGroup aria-label="Signed in as">
          <DropdownMenuLabel className="wrap-anywhere">{name}</DropdownMenuLabel>
        </DropdownMenuGroup>
        <DropdownMenuSeparator />
        <DropdownMenuItem onAction={() => logout.mutate()}>
          <HugeiconsIcon icon={Logout01Icon} />
          Logout
        </DropdownMenuItem>
      </DropdownMenu>
    </DropdownMenuTrigger>
  )
}
