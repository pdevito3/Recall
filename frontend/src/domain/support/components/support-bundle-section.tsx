import { Button } from '@/components/ui/button'
import { useMembership } from '@/domain/membership/apis/get-membership'

import { useDownloadSupportBundle } from '../apis/download-support-bundle'

/** Each Admin sees this, because the Server alone knows who the Owner is. The Server refuses each other Admin. */
export function SupportBundleSection() {
  const membership = useMembership()
  const download = useDownloadSupportBundle()

  if (membership.status !== 'member' || membership.user.role !== 'Admin') return null

  return (
    <section aria-labelledby="support-heading" className="space-y-3 border-t pt-4">
      <h2 id="support-heading" className="text-lg font-semibold">Support</h2>
      <p className="text-muted-foreground leading-relaxed">
        The support bundle is one zip with the logs, the telemetry, the version, and the settings with the secrets hidden. It holds no journal text.
      </p>
      <Button variant="outline" isPending={download.isPending} onPress={() => download.mutate()}>
        Download Support Bundle
      </Button>
    </section>
  )
}
