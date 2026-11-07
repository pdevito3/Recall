import { Alert02Icon, ArrowLeft01Icon, ArrowRight01Icon, CancelCircleIcon, CheckmarkCircle02Icon, Loading03Icon } from '@hugeicons/core-free-icons'
import type { IconSvgElement } from '@hugeicons/react'
import { HugeiconsIcon } from '@hugeicons/react'
import { useRef } from 'react'

import type { SummaryDto } from '@/api/generated/models/types/summary-dto'
import { QueryError } from '@/components/query-error'
import { Button } from '@/components/ui/button'
import { buttonVariants } from '@/components/ui/button-variants'
import { Link } from '@/components/ui/link'
import { browserTimeZone } from '@/lib/zoned-time'
import { cn } from '@/lib/utils'

import { useGenerateSummary, useSummary } from '../apis/summaries'
import type { SummaryPeriod } from '../types'
import { nextPeriodDate, paragraphsOf, periodNoun, periodTitle, previousPeriodDate, sourceCountText, writtenAtText } from './summary-words'

/** A Missing period with no sources is empty. A Missing period with sources has no Summary yet. */
type ShownStatus = SummaryDto['status'] | 'Empty'

interface StatusLine {
  text: string
  icon: IconSvgElement | null
  className: string
}

const STATUS_LINES: Record<ShownStatus, StatusLine> = {
  Empty: { text: 'No Sessions in this period.', icon: null, className: 'text-muted-foreground' },
  Missing: { text: 'This period has no Summary yet.', icon: null, className: 'text-muted-foreground' },
  Generating: { text: 'Writing the Summary. It can take a minute.', icon: Loading03Icon, className: 'text-foreground [&_svg]:motion-safe:animate-spin' },
  Ready: { text: 'Up to date.', icon: CheckmarkCircle02Icon, className: 'text-muted-foreground' },
  Stale: {
    text: 'Out of date. Something in this period changed after the Summary was written.',
    icon: Alert02Icon,
    className: 'text-foreground',
  },
  Failed: { text: 'This Summary was not written.', icon: CancelCircleIcon, className: 'text-destructive' },
}

const NO_PROVIDER_TEXT = 'An Admin must connect a model provider, pick a model, and make it active first.'

const FAILURE_TEXT: Record<SummaryDto['failure'], string> = {
  None: 'The run stopped with no result. Try again.',
  NoModelProvider: NO_PROVIDER_TEXT,
  KeyUnreadable: 'The server cannot read the API key of the model provider. An Admin must enter the key again.',
  ProviderRefused: 'The model provider refused the request. An Admin can test the connection on the Model providers page.',
  ProviderUnavailable: 'The model provider did not answer. Try again later.',
  BadModelOutput: 'The answer of the model is not one that the app can use. Try again.',
  Interrupted: 'The run stopped before it got a result. Try again.',
  SourceFailed: 'A shorter period in this one has no Summary, because its run failed. Try again, or open that period to see why.',
}

function shownStatusOf({ status, sourceCount }: SummaryDto): ShownStatus {
  return status === 'Missing' && sourceCount === 0 ? 'Empty' : status
}

function LoadingSummary() {
  return (
    <p role="status" aria-label="Loading the Summary" className="text-sm text-muted-foreground">
      Loading the Summary
    </p>
  )
}

interface SummaryBodyProps {
  summary: SummaryDto
  isGenerating: boolean
  onGenerate: () => void
}

function SummaryBody({ summary, isGenerating, onGenerate }: SummaryBodyProps) {
  const shown = shownStatusOf(summary)
  const line = STATUS_LINES[shown]
  const hasContent = summary.status === 'Ready' || summary.status === 'Stale'
  const facts = [
    summary.sourceCount > 0 ? sourceCountText(summary.period, summary.sourceCount) : null,
    hasContent && summary.generatedAt ? writtenAtText(summary.generatedAt, browserTimeZone()) : null,
  ].filter((fact) => fact !== null)

  return (
    <>
      <div className="flex flex-col gap-1">
        <p role="status" className={cn('flex items-start gap-2 text-sm', line.className)}>
          {line.icon && <HugeiconsIcon icon={line.icon} aria-hidden className="mt-0.5 size-4 shrink-0" />}
          <span>{line.text}</span>
        </p>
        {facts.length > 0 && <p className="text-sm text-muted-foreground">{facts.join(' · ')}</p>}
      </div>
      {summary.status === 'Failed' && (
        <div role="alert" className="flex flex-col items-start gap-3 rounded-lg border border-destructive/20 bg-destructive/10 p-4 text-sm text-destructive wrap-anywhere">
          <p>{FAILURE_TEXT[summary.failure]}</p>
          <Button variant="outline" isPending={isGenerating} onPress={onGenerate}>
            Retry
          </Button>
        </div>
      )}
      {summary.status === 'Stale' && (
        <Button variant="outline" className="self-start" isPending={isGenerating} onPress={onGenerate}>
          Regenerate
        </Button>
      )}
      {shown === 'Missing' && (
        <Button variant="outline" className="self-start" isPending={isGenerating} onPress={onGenerate}>
          Generate
        </Button>
      )}
      {hasContent && (
        <div className="flex flex-col gap-4 leading-7">
          {paragraphsOf(summary.content).map((paragraph, index) => (
            <p key={index} className="whitespace-pre-line wrap-anywhere">
              {paragraph}
            </p>
          ))}
        </div>
      )}
    </>
  )
}

interface SummaryPanelProps {
  period: SummaryPeriod
  date: string
}

export function SummaryPanel({ period, date }: SummaryPanelProps) {
  const params = { period, date }
  const summary = useSummary(params)
  const generate = useGenerateSummary(params)
  // Retry and Regenerate go away when the run starts, so the focus goes to the name of the period.
  const headingRef = useRef<HTMLHeadingElement>(null)

  if (summary.isPending) return <LoadingSummary />
  if (summary.isLoadingError) {
    return <QueryError title="The Summary did not load." error={summary.error} onRetry={() => void summary.refetch()} />
  }

  const { data } = summary
  const noun = periodNoun(period)
  const iconLinkClassName = buttonVariants({ variant: 'outline', size: 'icon', className: 'max-sm:size-11' })
  const handleGenerate = () => generate.mutate(undefined, { onSuccess: () => headingRef.current?.focus() })

  return (
    <div className="flex flex-col gap-4">
      <div className="flex flex-wrap items-center gap-x-4 gap-y-2">
        <h2 ref={headingRef} tabIndex={-1} className="min-w-0 flex-1 basis-56 text-xl font-semibold wrap-anywhere outline-none">
          {periodTitle(data)}
        </h2>
        <div className="flex items-center gap-2">
          <Link
            to="/summaries"
            search={{ period, date: previousPeriodDate(data) }}
            aria-label={`Previous ${noun}`}
            className={iconLinkClassName}
          >
            <HugeiconsIcon icon={ArrowLeft01Icon} aria-hidden />
          </Link>
          <Link to="/summaries" search={{ period }} className={buttonVariants({ variant: 'outline', className: 'max-sm:h-11' })}>
            Today
          </Link>
          <Link to="/summaries" search={{ period, date: nextPeriodDate(data) }} aria-label={`Next ${noun}`} className={iconLinkClassName}>
            <HugeiconsIcon icon={ArrowRight01Icon} aria-hidden />
          </Link>
        </div>
      </div>
      {summary.isPlaceholderData ? (
        <LoadingSummary />
      ) : (
        <SummaryBody summary={data} isGenerating={generate.isPending} onGenerate={handleGenerate} />
      )}
    </div>
  )
}
