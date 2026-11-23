import type { PersonDto } from '@/api/generated/models/types/person-dto'
import type { PersonProposalDto } from '@/api/generated/models/types/person-proposal-dto'

// The Server asks which Person to tag by the same rule (`PersonProposal.Matches`), so the card never offers a Tag that it refuses.
// The key is `Person.KeyOf`: `VisibleText.OneLine`, then `ToUpperInvariant`.

const isWhiteSpace = (c: string) => /[\t-\r\x85\p{Zs}\p{Zl}\p{Zp}]/u.test(c)

const isHidden = (c: string) =>
  /\p{Cc}/u.test(c) || c === '\u2028' || c === '\u2029' || (/\p{Cf}/u.test(c) && c !== '\u200C' && c !== '\u200D')

function oneLine(value: string): string {
  let result = ''
  for (let i = 0; i < value.length; i++) {
    const c = value[i]
    if (isHidden(c) && !isWhiteSpace(c)) continue
    if (!isWhiteSpace(c)) result += c
    else if (result.length > 0 && !result.endsWith(' ')) result += ' '
  }
  return result.trimEnd()
}

// .NET maps each character to one character, and keeps the dotless i, so a full mapping such as ß to SS does not apply.
// A Greek letter with an iota below is the one case where that simple mapping is not the character itself.
function upperInvariant(value: string): string {
  let result = ''
  for (const c of value) {
    const upper = c.toUpperCase()
    if (/[\u1F80-\u1F87\u1F90-\u1F97\u1FA0-\u1FA7]/.test(c)) result += String.fromCharCode(c.charCodeAt(0) + 8)
    else if (/[\u1FB3\u1FC3\u1FF3]/.test(c)) result += String.fromCharCode(c.charCodeAt(0) + 9)
    else result += c === '\u0131' || [...upper].length !== 1 ? c : upper
  }
  return result
}

const keyOf = (name: string) => upperInvariant(oneLine(name))

/** The People that an approve of the proposal can tag: its stored matches that still exist, then each other Person of its name. */
export function candidatesOf(proposal: PersonProposalDto, people: readonly PersonDto[]): PersonDto[] {
  const key = keyOf(proposal.label)
  const stored = proposal.matchedPersonIds.flatMap((id) => people.filter((person) => person.id === id))
  const byName = people.filter((person) => !proposal.matchedPersonIds.includes(person.id) && keyOf(person.label) === key)
  return [...stored, ...byName]
}
