import * as React from "react"
import { MenuTrigger as MenuTriggerPrimitive } from "react-aria-components"

function DropdownMenuTrigger({
  ...props
}: React.ComponentProps<typeof MenuTriggerPrimitive>) {
  return <MenuTriggerPrimitive {...props} />
}

export { DropdownMenuTrigger }
