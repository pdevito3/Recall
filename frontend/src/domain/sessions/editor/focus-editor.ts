export function focusEditorIn(section: HTMLElement | null) {
  section?.querySelector<HTMLElement>('[role="textbox"]')?.focus()
}
