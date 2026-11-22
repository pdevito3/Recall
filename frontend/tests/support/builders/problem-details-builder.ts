import type { ProblemDetails } from '@/lib/problem-details'

// The body of an ASP.NET error response. The SPA shows it as a problem only if `type`, `title`, and `status` are set.
class ProblemDetailsBuilder {
  private readonly problem: ProblemDetails

  private constructor(problem: ProblemDetails) {
    this.problem = problem
  }

  static create() {
    return new ProblemDetailsBuilder({
      type: 'https://tools.ietf.org/html/rfc9110#section-15.6.1',
      title: 'An error occurred while processing your request.',
      status: 500,
      traceId: '00-00000000000000000000000000000001-0000000000000001-01',
    })
  }

  saying(detail: string) {
    return new ProblemDetailsBuilder({ ...this.problem, detail })
  }

  staleVersion() {
    const detail = 'This record changed after you loaded it. Reload it, then make your change again.'
    return new ProblemDetailsBuilder({
      ...this.problem,
      type: 'urn:recall:problem:stale-version',
      title: 'The record changed after you loaded it.',
      status: 409,
      detail,
      errors: { version: [detail] },
    })
  }

  conflict(detail: string) {
    return new ProblemDetailsBuilder({
      ...this.problem,
      title: 'The request conflicts with a record that exists.',
      status: 409,
      detail,
    })
  }

  notFound(detail: string) {
    return new ProblemDetailsBuilder({
      ...this.problem,
      type: 'https://tools.ietf.org/html/rfc9110#section-15.5.5',
      title: 'The specified resource was not found.',
      status: 404,
      detail,
    })
  }

  refusing(detail: string) {
    return new ProblemDetailsBuilder({ ...this.problem, status: 422, title: 'Unprocessable Entity', detail })
  }

  forbidden(detail: string) {
    return new ProblemDetailsBuilder({
      ...this.problem,
      type: 'https://tools.ietf.org/html/rfc9110#section-15.5.4',
      title: 'You do not have access to do this.',
      status: 403,
      detail,
    })
  }

  /** The 403 of a link from a session that began more than 15 minutes ago (IDP-10). */
  signInAgainToLink() {
    return new ProblemDetailsBuilder({
      ...this.forbidden('Sign in again, then link the provider within 15 minutes.').problem,
      type: 'urn:recall:problem:sign-in-again-to-link',
    })
  }

  invalid(errors: Record<string, string[]>) {
    return new ProblemDetailsBuilder({
      ...this.problem,
      type: 'https://tools.ietf.org/html/rfc4918#section-11.2',
      title: 'One or more validation errors occurred.',
      status: 422,
      errors,
    })
  }

  build(): ProblemDetails {
    return this.problem
  }
}

export const aProblem = () => ProblemDetailsBuilder.create()
