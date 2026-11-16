/** Puts the text on the clipboard. It throws when the browser refuses the copy. */
export async function copyToClipboard(text: string): Promise<void> {
  try {
    await navigator.clipboard.writeText(text)
  } catch {
    // The clipboard API needs a secure context, and Recall can run on plain HTTP on a home network.
    const field = document.createElement('textarea')
    field.value = text
    document.body.append(field)
    field.select()
    const isCopied = document.execCommand('copy')
    field.remove()
    if (!isCopied) throw new Error('The browser refused the copy.')
  }
}
