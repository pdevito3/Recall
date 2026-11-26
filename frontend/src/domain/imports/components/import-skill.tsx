import { Notification } from '@/components/notification'
import { Button } from '@/components/ui/button'
import { Link } from '@/components/ui/link'
import { copyToClipboard } from '@/lib/copy-to-clipboard'

import skill from '../recall-import.md?raw'

/** The `recall-import` skill, which a person copies into their own agent. The SPA ships the file, so a test can check it against the MCP tools (IMP-30). */
export function ImportSkill() {
  const handleCopy = async () => {
    try {
      await copyToClipboard(skill)
      Notification.success('Skill copied')
    } catch {
      Notification.error('The browser did not copy the skill. Select the text of the skill, and copy it by hand.')
    }
  }

  return (
    <section aria-labelledby="import-skill-heading" className="flex flex-col gap-3">
      <div className="flex flex-wrap items-end justify-between gap-x-4 gap-y-2">
        <div className="flex max-w-prose min-w-0 flex-1 basis-72 flex-col gap-1">
          <h2 id="import-skill-heading" className="text-lg font-semibold">Import with an agent</h2>
          <p className="text-sm text-muted-foreground">
            Copy this skill into your agent, such as Claude Code or Claude Desktop. It tells the agent how to split your old journal into entries and put them in a batch. Connect the agent to Recall with a{' '}
            <Link to="/settings/personal-access-tokens" className="underline underline-offset-4 hover:text-primary">personal access token</Link>
            {' '}that can change your journal.
          </p>
        </div>
        <Button variant="outline" onPress={() => void handleCopy()}>
          Copy skill
        </Button>
      </div>
      {/* Chromium and Firefox let a keyboard focus a box that scrolls. Safari does not, so the box takes the focus itself. */}
      <pre
        tabIndex={0}
        aria-label="The recall-import skill"
        className="max-h-96 overflow-y-auto rounded-lg border bg-muted p-3 font-mono text-xs leading-relaxed whitespace-pre-wrap wrap-anywhere focus-visible:ring-3 focus-visible:ring-ring/50 focus-visible:outline-none"
      >
        {skill}
      </pre>
    </section>
  )
}
