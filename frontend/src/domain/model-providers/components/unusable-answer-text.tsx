import { Link } from '@/components/ui/link'
import { useCanManageModelProviders } from '@/domain/membership/apis/get-membership'

/**
 * The words for an answer that the app cannot use. The cause is often a small model, but not always, so the text says "most often".
 * A person who can change the model gets a link to the page. While the membership loads, the last sentence waits, so the words only grow.
 */
export function UnusableAnswerText() {
  const canManageModelProviders = useCanManageModelProviders()

  return (
    <>
      The model provider sent an answer that the app cannot use. Most often the model is too small for this work. Try again.
      {canManageModelProviders === true && (
        <>
          {' '}
          If it happens often, pick a stronger model or test the connection on the{' '}
          <Link to="/model-providers" className="font-medium underline underline-offset-4">
            Model providers
          </Link>{' '}
          page.
        </>
      )}
      {canManageModelProviders === false && ' If it happens often, an Admin can pick a stronger model or test the connection.'}
    </>
  )
}
