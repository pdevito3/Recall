import { httpsGuideUrl } from '../https-guide'

export function InsecureContextNotice() {
  if (window.isSecureContext) return null

  return (
    <section
      aria-label="Mic"
      className="mb-4 rounded-lg border border-border bg-muted px-4 py-3 text-sm text-foreground"
    >
      <p>
        The mic needs HTTPS. This page opened on plain HTTP, so the mic stays off.{' '}
        <a href={httpsGuideUrl} target="_blank" rel="noreferrer" className="font-medium text-primary underline underline-offset-4">
          Turn on HTTPS
        </a>
      </p>
    </section>
  )
}
