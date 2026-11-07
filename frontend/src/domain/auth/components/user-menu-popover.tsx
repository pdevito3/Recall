import { Logout01Icon } from '@hugeicons/core-free-icons'
import { HugeiconsIcon } from '@hugeicons/react'

import {
  DropdownMenu,
  DropdownMenuGroup,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuSeparator,
} from '@/components/ui/dropdown-menu'

export function UserMenuPopover({ name, onLogout }: { name: string; onLogout: () => void }) {
  return (
    <DropdownMenu aria-label="User menu" placement="top start" className="w-56">
      <DropdownMenuGroup aria-label="Signed in as">
        <DropdownMenuLabel className="wrap-anywhere">{name}</DropdownMenuLabel>
      </DropdownMenuGroup>
      <DropdownMenuSeparator />
      <DropdownMenuItem onAction={onLogout}>
        <HugeiconsIcon icon={Logout01Icon} />
        Logout
      </DropdownMenuItem>
    </DropdownMenu>
  )
}
