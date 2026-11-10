import { DialogTrigger, type DialogTriggerProps } from "react-aria-components"

function PopoverTrigger({ children, ...props }: DialogTriggerProps) {
  return (
    <DialogTrigger {...props}>
      {children}
    </DialogTrigger>
  )
}

export { PopoverTrigger }
