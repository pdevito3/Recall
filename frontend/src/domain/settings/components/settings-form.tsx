import { useQueryClient } from '@tanstack/react-query'
import { useMemo, useSyncExternalStore } from 'react'
import { useFilter } from 'react-aria-components'

import { QueryError } from '@/components/query-error'
import { Checkbox } from '@/components/ui/checkbox'
import { Combobox, ComboboxContent, ComboboxEmpty, ComboboxInput, ComboboxItem, ComboboxList } from '@/components/ui/combobox'
import { Skeleton } from '@/components/ui/skeleton'
import { SaveStatusLine } from '@/domain/sessions/components/save-status'
import { StaleChangePrompts } from '@/domain/sessions/components/stale-change-prompts'
import { userSettingDtoTimeZoneModeEnum } from '@/api/generated/models/types/user-setting-dto'
import { followsTheDevice, journalZoneOf, settingSaver, useUserSetting, zoneOfNewEntries, type SettingChange } from '@/domain/settings/apis/user-setting'
import { useCurrentUser } from '@/domain/users/apis/get-current-user'
import { deviceTimeZone, timeZoneLabel, type TimeZone } from '@/lib/zoned-time'

interface ZoneOption {
  id: string
  label: string
}

const placeOf = (zone: string) => zone.replaceAll('/', ' / ')

function zoneOption(id: string): ZoneOption {
  try {
    return { id, label: `${placeOf(id)} (${timeZoneLabel(id as TimeZone)})` }
  } catch {
    return { id, label: placeOf(id) }
  }
}

const automaticKey = 'automatic'

const automaticOption = (zone: TimeZone | null): ZoneOption => ({ id: automaticKey, label: zone ? `Automatic (${placeOf(zone)})` : 'Automatic' })

const withoutZoneSeparators = (text: string) => text.replace(/[\s/_]+/g, '')

let listedZones: ZoneOption[] | undefined

// The list of the browser leaves out UTC and the new name of a renamed zone, such as Asia/Kolkata. A label costs about 0.5 ms.
function zoneOptions(savedZone: string | null | undefined): ZoneOption[] {
  listedZones ??= [...new Set(['UTC', ...Intl.supportedValuesOf('timeZone')])].sort().map(zoneOption)
  if (!savedZone || listedZones.some(({ id }) => id === savedZone)) return listedZones
  return [...listedZones, zoneOption(savedZone)].sort((a, b) => (a.id < b.id ? -1 : 1))
}

const settingLabels: Record<keyof SettingChange, string> = {
  timeZoneMode: 'Time zone',
  timeZone: 'Time zone',
  locationCaptureEnabled: 'Save my location with each new entry',
  requirePeopleProposalApproval: 'Ask me before AI adds a person to an entry',
  removeFillerWords: 'Remove filler words, such as “um” and “uh”, from Cleaned',
}

export function SettingsForm() {
  const setting = useUserSetting()
  const queryClient = useQueryClient()
  const saver = settingSaver(queryClient)
  const status = useSyncExternalStore(saver.subscribe, saver.getStatus)
  const asks = useSyncExternalStore(saver.subscribe, saver.asks)
  const { contains } = useFilter({ sensitivity: 'base' })
  const zone = journalZoneOf(setting.data)
  const deviceZone = deviceTimeZone()
  const fixedZone = setting.data && !followsTheDevice(setting.data) ? setting.data.timeZone : null
  const zones = useMemo(() => [automaticOption(deviceZone), ...zoneOptions(fixedZone)], [deviceZone, fixedZone])

  if (setting.isPending) {
    return <p role="status" aria-label="Loading your settings" className="text-muted-foreground">Loading your settings</p>
  }
  if (setting.isError) {
    return <QueryError title="Your settings did not load." error={setting.error} onRetry={() => setting.refetch()} />
  }

  const values: SettingChange = { ...setting.data, ...saver.pendingFields() }
  const change = (patch: Partial<SettingChange>) => saver.edit(patch)
  const zoneInUse = zoneOfNewEntries({ ...setting.data, ...values })

  return (
    <div className="flex flex-col gap-8">
      <StaleChangePrompts asks={asks} labelOf={(field) => settingLabels[field]} onAnswer={(ask, usesYours) => saver.answerAsk(ask, usesYours)} />
      <section className="flex flex-col gap-2" aria-labelledby="settings-zone">
        <h2 id="settings-zone" className="text-lg font-semibold">Time zone</h2>
        <p className="text-sm text-muted-foreground">Your time zone decides the day of each new entry. Automatic follows the time zone of the device that you use, and an entry keeps its day when you travel.</p>
        {setting.data.timeZone && !setting.data.timeZoneIsKnown && (
          <p role="alert" className="text-sm text-destructive wrap-anywhere">
            This server does not know your saved time zone, so your days use UTC. Pick a time zone to fix your days.
          </p>
        )}
        <Combobox
          aria-label="Time zone"
          aria-describedby="settings-zone-in-use"
          defaultItems={zones}
          defaultFilter={(text, search) => contains(withoutZoneSeparators(text), withoutZoneSeparators(search))}
          allowsEmptyCollection
          selectedKey={followsTheDevice(values) ? automaticKey : (values.timeZone ?? null)}
          onSelectionChange={(key) => {
            if (key === automaticKey) change({ timeZoneMode: userSettingDtoTimeZoneModeEnum.FollowDevice, timeZone: null })
            else if (typeof key === 'string') change({ timeZoneMode: userSettingDtoTimeZoneModeEnum.Fixed, timeZone: key })
          }}
        >
          <ComboboxInput className="[&_input]:text-ellipsis" />
          <ComboboxContent>
            <ComboboxList renderEmptyState={() => <ComboboxEmpty>No time zone matches your search.</ComboboxEmpty>}>
              {(item: ZoneOption) => <ComboboxItem id={item.id} textValue={item.label}>{item.label}</ComboboxItem>}
            </ComboboxList>
          </ComboboxContent>
        </Combobox>
        <p id="settings-zone-in-use" className="text-sm text-muted-foreground wrap-anywhere">
          New entries use the day in {zoneOption(zoneInUse).label}.
        </p>
      </section>
      <section className="flex flex-col gap-3" aria-labelledby="settings-entries">
        <div className="flex flex-wrap items-baseline justify-between gap-x-4">
          <h2 id="settings-entries" className="text-lg font-semibold">Entries</h2>
          {(asks.length === 0 || status.kind !== 'saved') && <SaveStatusLine status={status} zone={zone} />}
        </div>
        <Checkbox
          className="max-md:min-h-11"
          isSelected={values.locationCaptureEnabled}
          onChange={(locationCaptureEnabled) => change({ locationCaptureEnabled })}
        >
          Save my location with each new entry
        </Checkbox>
        <Checkbox
          className="max-md:min-h-11"
          isSelected={values.requirePeopleProposalApproval}
          onChange={(requirePeopleProposalApproval) => change({ requirePeopleProposalApproval })}
        >
          Ask me before AI adds a person to an entry
        </Checkbox>
        <Checkbox
          className="max-md:min-h-11"
          isSelected={values.removeFillerWords}
          onChange={(removeFillerWords) => change({ removeFillerWords })}
        >
          Remove filler words, such as “um” and “uh”, from Cleaned
        </Checkbox>
      </section>
      <section className="flex flex-col gap-2" aria-labelledby="settings-account">
        <h2 id="settings-account" className="text-lg font-semibold">Account</h2>
        <SignedInUser />
      </section>
    </div>
  )
}

function SignedInUser() {
  const { data: user, isLoading, isError } = useCurrentUser()

  if (isLoading) {
    return <Skeleton className="h-5 w-48" aria-label="Loading your user" />
  }
  if (isError || !user) {
    return <p className="text-sm text-destructive">The Server could not load your user.</p>
  }
  return (
    <p className="min-w-0 text-sm wrap-anywhere">
      Signed in as <span className="font-medium">{user.name ?? user.identifier}</span>
    </p>
  )
}
