/** Puts the text on the clipboard. It throws when the browser refuses the copy. */
export async function copyToClipboard(text: string): Promise<void> {
  try {
    await navigator.clipboard.writeText(text)
  } catch {
    // The clipboard API needs a secure context, and Recall can run on plain HTTP on a home network.
    const field = document.createElement('textarea')
    field.value = text
    // On iOS a field that can take text raises the keyboard, and a font under 16px zooms the page.
    field.readOnly = true
    field.style.cssText = 'position:fixed;top:0;left:0;opacity:0;font-size:16px'
    // The focus trap of a modal takes the focus back from a field outside it, and the copy then copies nothing.
    const host = document.activeElement?.closest('[role=dialog]') ?? document.body
    host.append(field)
    field.select()
    const isCopied = document.execCommand('copy')
    field.remove()
    if (!isCopied) throw new Error('The browser refused the copy.')
  }
}
