import { Checkbox } from '@/components/ui/checkbox'
import { useUserSetting } from '@/domain/settings/apis/user-setting'

import { useChangeImportReview } from '../apis/import-review'

export function ImportReviewSetting() {
  const setting = useUserSetting()
  const change = useChangeImportReview()

  if (!setting.data) return null
  const isOn = change.isPending ? change.variables : setting.data.requireImportReview

  return (
    <section className="flex flex-col gap-2" aria-labelledby="settings-imports">
      <h2 id="settings-imports" className="text-lg font-semibold">Imports</h2>
      <p id="settings-imports-review" className="text-sm text-muted-foreground">
        With this on, only you can accept an import batch, in Recall in a browser. With it off, an agent with a personal access token can accept a
        batch too.
      </p>
      <Checkbox
        className="max-md:py-3.5"
        aria-describedby="settings-imports-review"
        isSelected={isOn}
        isDisabled={change.isPending}
        onChange={(requireImportReview) => change.mutate(requireImportReview)}
      >
        Only I can accept an import batch
      </Checkbox>
      {change.isError && (
        <p role="alert" className="text-sm text-destructive wrap-anywhere">{change.error.message}</p>
      )}
    </section>
  )
}
