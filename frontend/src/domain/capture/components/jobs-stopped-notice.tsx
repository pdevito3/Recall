import { useTranscriptionStatus } from '../apis/get-transcription-status'

export function JobsStoppedNotice() {
  const text = useTranscriptionStatus().data?.jobsStoppedText
  if (!text) return null

  return (
    <section role="alert" aria-label="Background work" className="rounded-lg border border-border bg-muted px-4 py-3 text-sm text-foreground">
      <p>{text}</p>
    </section>
  )
}
