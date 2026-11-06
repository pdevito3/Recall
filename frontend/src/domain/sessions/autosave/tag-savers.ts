/** The savers of open tags, apart from `metadata-saver.ts`, because the first page must not load the tag code to warn at a close. */
interface TagSaver {
  hasUnsaved: () => boolean
  settle: () => Promise<boolean>
  dispose: () => void
}

const savers = new Set<TagSaver>()

export const noteTagSaver = (saver: TagSaver) => void savers.add(saver)

export const forgetTagSaver = (saver: TagSaver) => void savers.delete(saver)

export const isAnyTagChangeUnsaved = () => [...savers].some((saver) => saver.hasUnsaved())

export const stopMetadataSavers = () => {
  for (const saver of savers) saver.dispose()
}

export const settleMetadataSavers = async () => (await Promise.all([...savers].map((saver) => saver.settle()))).every(Boolean)
