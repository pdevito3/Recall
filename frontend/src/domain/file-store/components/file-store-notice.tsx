import { useFileStoreStatus } from '../apis/get-file-store-status'

export function FileStoreNotice() {
  const status = useFileStoreStatus()

  if (status.data?.canWrite !== false) return null

  return (
    <section
      aria-label="File store"
      role="alert"
      className="mb-4 space-y-1 border-y border-destructive/20 bg-destructive/10 px-4 py-3 text-sm text-destructive"
    >
      <p className="font-medium">Recall cannot write to its file store, so it cannot keep new audio recordings.</p>
      <p>
        Check that the computer that runs Recall has free disk space and that its storage folder is available. This message goes
        away when writes work again.
      </p>
    </section>
  )
}
