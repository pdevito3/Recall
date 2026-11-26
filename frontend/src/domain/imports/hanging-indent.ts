/**
 * The width, in characters, of the start of a line of Markdown that a wrapped line lines up under: the indent, and
 * the marker of a list item with its space, such as `- ` or `10. `. So a wrapped list item in a box of plain text
 * starts under its own words, not under its marker.
 */
export function hangingIndentOf(line: string): number {
  return /^ *(?:(?:[-*+]|\d+\.) +)?/.exec(line)?.[0].length ?? 0
}
