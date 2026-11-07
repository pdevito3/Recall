import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'

import { describe, expect, it } from 'vitest'

const css = readFileSync(resolve(__dirname, 'index.css'), 'utf8')

function luminanceOf(token: string, block: string) {
  const found = new RegExp(`--${token}: oklch\\(([\\d.]+) ([\\d.]+) ([\\d.]+)\\)`).exec(block)
  if (!found) throw new Error(`No token ${token}.`)
  const [light, chroma, hue] = found.slice(1).map(Number)
  const a = chroma * Math.cos((hue * Math.PI) / 180)
  const b = chroma * Math.sin((hue * Math.PI) / 180)
  const l = (light + 0.3963377774 * a + 0.2158037573 * b) ** 3
  const m = (light - 0.1055613458 * a - 0.0638541728 * b) ** 3
  const s = (light - 0.0894841775 * a - 1.291485548 * b) ** 3
  const red = Math.max(4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s, 0)
  const green = Math.max(-1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s, 0)
  const blue = Math.max(-0.0041960863 * l - 0.7034186147 * m + 1.707614701 * s, 0)
  return 0.2126 * red + 0.7152 * green + 0.0722 * blue
}

function contrastOf(first: number, second: number) {
  return (Math.max(first, second) + 0.05) / (Math.min(first, second) + 0.05)
}

const blockOf = (selector: string) => css.slice(css.indexOf(`${selector} {`), css.indexOf('}', css.indexOf(`${selector} {`)))

describe('the primary color', () => {
  it.each([':root', '.dark'])('has words that a person can read, and it reads on the page, in %s', (selector) => {
    const block = blockOf(selector)
    const primary = luminanceOf('primary', block)

    expect(contrastOf(primary, luminanceOf('primary-foreground', block))).toBeGreaterThanOrEqual(4.5)
    expect(contrastOf(primary, luminanceOf('background', block))).toBeGreaterThanOrEqual(4.5)
  })
})
