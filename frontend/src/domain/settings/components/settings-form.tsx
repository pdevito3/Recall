import { useQueryClient } from '@tanstack/react-query'
import { useMemo, useSyncExternalStore } from 'react'

import { QueryError } from '@/components/query-error'
import { Checkbox } from '@/components/ui/checkbox'
import { Combobox, ComboboxContent, ComboboxInput, ComboboxItem, ComboboxList } from '@/components/ui/combobox'
import { Label } from '@/components/ui/label'
import { Skeleton } from '@/components/ui/skeleton'
import { SaveStatusLine } from '@/domain/sessions/components/save-status'
import { journalZoneOf, settingSaver, useUserSetting, type SettingChange } from '@/domain/settings/apis/user-setting'
import { useCurrentUser } from '@/domain/users/apis/get-current-user'
import { parseTimeZone, timeZoneLabel, type TimeZone } from '@/lib/zoned-time'

function zoneOptions(): { id: string; label: string }[] {
  return Intl.supportedValuesOf('timeZone').map((id) => ({ id, label: `${id.replaceAll('/', ' / ')} (${timeZoneLabel(id as TimeZone)})` }))
}

export function SettingsForm() {
  const setting = useUserSetting()
  const queryClient = useQueryClient()
  const saver = settingSaver(queryClient)
  const status = useSyncExternalStore(saver.subscribe, saver.getStatus)
  const zones = useMemo(zoneOptions, [])

  if (setting.isPending) {
    return <p role="status" aria-label="Loading your settings" className="text-muted-foreground">Loading your settings</p>
  }
  if (setting.isError) {
    return <QueryError title="Your settings did not load." error={setting.error} onRetry={() => setting.refetch()} />
  }

  const values: SettingChange = saver.newestUnsettledValue() ?? setting.data
  const change = (patch: Partial<SettingChange>) =>
    saver.edit({
      timeZone: values.timeZone,
      locationCaptureEnabled: values.locationCaptureEnabled,
      requirePeopleProposalApproval: values.requirePeopleProposalApproval,
      ...patch,
    })

  const zone = journalZoneOf(setting.data)

  return (
    <div className="flex max-w-prose flex-col gap-8">
      <section className="flex flex-col gap-2" aria-labelledby="settings-zone">
        <h2 id="settings-zone" className="text-lg font-semibold">Time zone</h2>
        <p className="text-sm text-muted-foreground">Your time zone decides the day that each entry belongs to.</p>
        {setting.data.timeZone && !setting.data.timeZoneIsKnown && (
          <p role="alert" className="text-sm text-destructive wrap-anywhere">
            This server does not know your saved time zone, so your days use UTC. Pick a time zone to fix your days.
          </p>
        )}
        <Combobox
          aria-label="Time zone"
          items={zones}
          selectedKey={values.timeZone ?? null}
          onSelectionChange={(key) => {
            if (typeof key === 'string') change({ timeZone: parseTimeZone(key) })
          }}
        >
          <Label className="sr-only">Time zone</Label>
          <ComboboxInput />
          <ComboboxContent>
            <ComboboxList>
              {(item: { id: string; label: string }) => <ComboboxItem id={item.id} textValue={item.label}>{item.label}</ComboboxItem>}
            </ComboboxList>
          </ComboboxContent>
        </Combobox>
      </section>
      <section className="flex flex-col gap-3" aria-labelledby="settings-entries">
        <h2 id="settings-entries" className="text-lg font-semibold">Entries</h2>
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
        <SaveStatusLine status={status} zone={zone} />
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
