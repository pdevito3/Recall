import * as React from "react"
import { TooltipTrigger as TooltipTriggerPrimitive } from "react-aria-components"

function TooltipTrigger({
  delay = 0,
  ...props
}: React.ComponentProps<typeof TooltipTriggerPrimitive>) {
  return (
    <TooltipTriggerPrimitive
      delay={delay}
      {...props}
    />
  )
}

export { TooltipTrigger }
