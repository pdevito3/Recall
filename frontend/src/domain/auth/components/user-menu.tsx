import { UserCircleIcon } from '@hugeicons/core-free-icons'
import { HugeiconsIcon } from '@hugeicons/react'

import { DropdownMenuTrigger } from '@/components/ui/dropdown-menu-trigger'
import { lazyOverlay } from '@/components/ui/lazy-overlay'
import { SidebarMenuButton } from '@/components/ui/sidebar'
import { useSidebar } from '@/components/ui/use-sidebar'

import { useLogout } from '../apis/auth-actions'
import { useAuth } from '../apis/get-user'

const UserMenuPopover = lazyOverlay(async () => (await import('./user-menu-popover')).UserMenuPopover)

export function UserMenu() {
  const { username } = useAuth()
  const logout = useLogout()
  const { setOpenMobile } = useSidebar()
  const name = username ?? 'Your account'

  return (
    <DropdownMenuTrigger>
      <SidebarMenuButton tooltip={name} aria-label={`User menu for ${name}`}>
        <HugeiconsIcon icon={UserCircleIcon} />
        <span className="truncate">{name}</span>
      </SidebarMenuButton>
      <UserMenuPopover
        name={name}
        onLogout={() => {
          // On a phone the sidebar is a sheet over the page, and the toast of the sign-out must not lie over it.
          setOpenMobile(false)
          logout.mutate()
        }}
      />
    </DropdownMenuTrigger>
  )
}
