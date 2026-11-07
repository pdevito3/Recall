import type { Key } from 'react-aria-components'
import { Tab, TabList, TabPanel, Tabs } from 'react-aria-components'

import { PageHeading } from '@/components/page-heading'
import { browserTimeZone, todayAt } from '@/lib/zoned-time'

import type { SummaryPeriod } from '../types'
import { SUMMARY_PERIODS } from './summary-words'
import { SummaryPanel } from './summary-panel'

interface SummariesPageProps {
  period: SummaryPeriod
  /** A date in the period, as yyyy-MM-dd. With none, the page shows the period of today. */
  date: string | undefined
  onPeriodChange: (period: SummaryPeriod) => void
}

export function SummariesPage({ period, date, onPeriodChange }: SummariesPageProps) {
  // A Summary belongs to the person, not to a place, so today is the date of the browser.
  const shownDate = date ?? todayAt(browserTimeZone(), new Date()).toString()
  const handleSelectionChange = (key: Key) => {
    const picked = SUMMARY_PERIODS.find((each) => each === key)
    if (picked) onPeriodChange(picked)
  }

  return (
    <div className="mx-auto flex w-full max-w-3xl flex-col gap-6 px-4 py-8 max-sm:[&_[data-slot=button]]:h-11 max-sm:[&_[data-slot=button]]:min-w-11">
      <PageHeading>Summaries</PageHeading>
      {/* Manual activation: an arrow key moves the focus, and only Enter or Space opens a period and starts its run. */}
      <Tabs selectedKey={period} onSelectionChange={handleSelectionChange} keyboardActivation="manual" className="flex flex-col gap-6">
        <TabList aria-label="Period" className="grid grid-cols-5 gap-1 self-stretch rounded-lg bg-muted p-1 sm:self-start">
          {SUMMARY_PERIODS.map((each) => (
            <Tab
              key={each}
              id={each}
              className="flex h-8 cursor-default items-center justify-center rounded-md px-3 text-sm font-medium text-muted-foreground outline-none transition-colors hover:text-foreground data-focus-visible:ring-3 data-focus-visible:ring-ring/50 data-selected:bg-background data-selected:text-foreground data-selected:shadow-sm max-sm:h-11 max-sm:px-1"
            >
              {each}
            </Tab>
          ))}
        </TabList>
        {SUMMARY_PERIODS.map((each) => (
          <TabPanel key={each} id={each} className="rounded-lg outline-none data-focus-visible:ring-3 data-focus-visible:ring-ring/50">
            <SummaryPanel period={each} date={shownDate} />
          </TabPanel>
        ))}
      </Tabs>
    </div>
  )
}
