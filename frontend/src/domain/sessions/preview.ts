const serverPreviewLength = 200

const shortestCut = serverPreviewLength / 2

export function shownPreviewOf(preview: string): string {
  if ([...preview].length < serverPreviewLength) return preview
  const lastSpace = preview.search(/\s\S*$/)
  const kept = lastSpace >= shortestCut ? preview.slice(0, lastSpace) : preview
  return `${kept.trimEnd()}…`
}
