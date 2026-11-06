export const FileStoreKeys = {
  all: ['file-store'] as const,
  status: () => [...FileStoreKeys.all, 'status'] as const,
}
