/** Session.PreviewMaxLength on the Server, which cuts a preview at this many characters. */
const serverPreviewLength = 200

const shortestCut = serverPreviewLength / 2

/** A preview at the limit of the Server can stop in the middle of a word, so it ends at a word and shows the cut. */
export function shownPreviewOf(preview: string): string {
  if ([...preview].length < serverPreviewLength) return preview
  const lastSpace = preview.search(/\s\S*$/)
  const kept = lastSpace >= shortestCut ? preview.slice(0, lastSpace) : preview
  return `${kept.trimEnd()}…`
}
