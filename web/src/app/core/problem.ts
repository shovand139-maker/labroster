import { HttpErrorResponse } from '@angular/common/http';

/** An RFC 9457 problem, as every LabRoster API error is shaped. */
export interface ApiProblem {
  status: number;
  code: string;
  title: string;
  detail: string;
  errors: Record<string, string[]>;
}

const OFFLINE: ApiProblem = {
  status: 0,
  code: 'network_error',
  title: 'Network error',
  detail: 'The server could not be reached. Check your connection and try again.',
  errors: {},
};

export function toProblem(error: unknown): ApiProblem {
  if (!(error instanceof HttpErrorResponse)) {
    return { ...OFFLINE, code: 'client_error', title: 'Something went wrong', detail: String(error) };
  }

  if (error.status === 0) {
    return OFFLINE;
  }

  const body = (typeof error.error === 'object' && error.error) || {};
  return {
    status: error.status,
    code: typeof body.code === 'string' ? body.code : `http_${error.status}`,
    title: typeof body.title === 'string' ? body.title : error.statusText || 'Request failed',
    detail: typeof body.detail === 'string' ? body.detail : (body.title ?? 'The request failed.'),
    errors: isErrorMap(body.errors) ? body.errors : {},
  };
}

/** Field errors keyed by API field name, flattened to one message per field. */
export function fieldErrors(problem: ApiProblem): Record<string, string> {
  return Object.fromEntries(
    Object.entries(problem.errors)
      .filter(([, messages]) => messages.length > 0)
      .map(([field, messages]) => [field, messages[0]]),
  );
}

/** Transient failures that are safe to retry with the same idempotency key. */
export function isRetryable(error: unknown): boolean {
  return error instanceof HttpErrorResponse && (error.status === 0 || error.status === 503 || error.status === 504);
}

function isErrorMap(value: unknown): value is Record<string, string[]> {
  return (
    typeof value === 'object' &&
    value !== null &&
    Object.values(value).every((v) => Array.isArray(v) && v.every((m) => typeof m === 'string'))
  );
}
