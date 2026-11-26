import { ArrowRight01Icon } from '@hugeicons/core-free-icons'
import { HugeiconsIcon } from '@hugeicons/react'
import { useRef, useState } from 'react'
import { flushSync } from 'react-dom'

import { Notification } from '@/components/notification'
import { Button } from '@/components/ui/button'
import { buttonVariants } from '@/components/ui/button-variants'
import { Collapsible, CollapsibleContent, CollapsibleTrigger } from '@/components/ui/collapsible'
import { Link } from '@/components/ui/link'
import { copyToClipboard } from '@/lib/copy-to-clipboard'

import { hangingIndentOf } from '../hanging-indent'
import skill from '../recall-import.md?raw'

// Each line keeps its line break, so the text of the box, and a copy of a selection in it, is the file.
const skillLines = skill.split(/(?<=\n)/)

interface CopyRowProps {
  label: string
  value: string
  copyLabel: string
  copiedMessage: string
  refusedMessage: string
}

/** A value with its copy button. When the browser refuses the copy, the row selects the value, so the person copies it by hand. */
function CopyRow({ label, value, copyLabel, copiedMessage, refusedMessage }: CopyRowProps) {
  const valueRef = useRef<HTMLElement>(null)

  const handleCopy = async () => {
    try {
      await copyToClipboard(value)
      Notification.success(copiedMessage)
    } catch {
      if (valueRef.current) window.getSelection()?.selectAllChildren(valueRef.current)
      Notification.error(refusedMessage)
    }
  }

  return (
    <div role="group" aria-label={label} className="flex flex-wrap items-center justify-between gap-x-3 gap-y-2 rounded-lg border p-3 sm:flex-nowrap">
      <code ref={valueRef} className="min-w-0 font-mono text-sm wrap-anywhere select-all">
        {/* A line can break after a hyphen, so each flag of a command, such as --header, stays on one line. */}
        {value.split(/(\s+)/).map((part, index) => (part.startsWith('-') ? <span key={index} className="whitespace-nowrap">{part}</span> : part))}
      </code>
      <Button variant="outline" onPress={() => void handleCopy()}>
        {copyLabel}
      </Button>
    </div>
  )
}

/** The MCP URL, the Claude Code command, and the `recall-import` skill, which a person gives their own agent. The SPA ships the skill file, so a test can check it against the MCP tools (IMP-30). */
export function ImportSkill() {
  const [isExpanded, setIsExpanded] = useState(false)
  const skillRef = useRef<HTMLPreElement>(null)
  // The Server serves the SPA and /mcp from one origin, and the dev server sends /mcp on to the Server.
  const mcpUrl = `${window.location.origin}/mcp`
  const claudeCodeCommand = `claude mcp add --transport http recall ${mcpUrl} --header "Authorization: Bearer <your token>"`

  const handleCopySkill = async () => {
    try {
      await copyToClipboard(skill)
      Notification.success('Skill copied')
    } catch {
      // Select all in the box selects the whole page, so the page selects the text of the skill for the person.
      flushSync(() => setIsExpanded(true))
      if (skillRef.current) window.getSelection()?.selectAllChildren(skillRef.current)
      Notification.error('The browser did not copy the skill. Recall selected the whole text of the skill, so copy it by hand.')
    }
  }

  return (
    <section aria-labelledby="import-skill-heading" className="flex flex-col gap-3">
      <h2 id="import-skill-heading" className="text-lg font-semibold">Import with an agent</h2>
      <p className="max-w-prose text-muted-foreground">
        Connect your agent to this MCP URL with a{' '}
        <Link to="/settings/personal-access-tokens" className="underline underline-offset-4 hover:text-primary">personal access token</Link>
        {' '}that has Change my journal. The agent sends the token in the header <code className="font-mono text-sm wrap-anywhere">Authorization: Bearer &lt;token&gt;</code>.
      </p>
      <CopyRow
        label="MCP URL"
        value={mcpUrl}
        copyLabel="Copy MCP URL"
        copiedMessage="MCP URL copied"
        refusedMessage="The browser did not copy the MCP URL. Recall selected it, so copy it by hand."
      />
      <p className="max-w-prose text-muted-foreground">
        For Claude Code, run this command with your token in place of &lt;your token&gt;.
      </p>
      <CopyRow
        label="Claude Code command"
        value={claudeCodeCommand}
        copyLabel="Copy command"
        copiedMessage="Command copied"
        refusedMessage="The browser did not copy the command. Recall selected it, so copy it by hand."
      />
      <p className="max-w-prose text-muted-foreground">
        Then give the agent the recall-import skill. It tells the agent how to split your old journal into entries and put them in a batch. Copy the skill, and save it as a skill of the agent, or paste it at the start of a chat.
      </p>
      <Collapsible isExpanded={isExpanded} onExpandedChange={setIsExpanded} className="rounded-lg border">
        <div className="flex flex-wrap items-center justify-between gap-x-3 gap-y-2 p-3">
          {/* The ghost trigger has its own padding, so its margin moves its arrow to the left edge of the text in the rows above. */}
          <CollapsibleTrigger className={buttonVariants({ variant: 'ghost', className: '-ml-2.75' })}>
            <HugeiconsIcon icon={ArrowRight01Icon} className="transition-transform group-aria-expanded/button:rotate-90 motion-reduce:transition-none" />
            The recall-import skill
          </CollapsibleTrigger>
          <Button variant="outline" onPress={() => void handleCopySkill()}>
            Copy skill
          </Button>
        </div>
        <CollapsibleContent>
          {/* Chromium and Firefox let a keyboard focus a box that scrolls. Safari does not, so the box takes the focus itself.
              The shared ring is too faint on the fill of the box, so the box draws its focus in the primary color.
              Each line is a block with a hanging indent, so a wrapped list item starts under its words. */}
          <pre
            ref={skillRef}
            role="region"
            tabIndex={0}
            aria-label="The text of the recall-import skill"
            className="max-h-96 overflow-y-auto rounded-b-lg border-t bg-muted p-3 font-mono text-xs leading-relaxed whitespace-pre-wrap wrap-anywhere focus-visible:ring-2 focus-visible:ring-primary focus-visible:ring-inset focus-visible:outline-none"
          >
            {skillLines.map((line, index) => (
              <span key={index} className="block" style={{ paddingLeft: `${hangingIndentOf(line)}ch`, textIndent: `-${hangingIndentOf(line)}ch` }}>
                {line}
              </span>
            ))}
          </pre>
        </CollapsibleContent>
      </Collapsible>
    </section>
  )
}
