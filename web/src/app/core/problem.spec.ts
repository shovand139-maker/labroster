import { HttpErrorResponse } from '@angular/common/http';
import { fieldErrors, isRetryable, toProblem } from './problem';

describe('toProblem', () => {
  it('reads code, detail and field errors from an RFC 9457 body', () => {
    const problem = toProblem(
      new HttpErrorResponse({
        status: 400,
        error: {
          title: 'One or more fields are invalid.',
          status: 400,
          code: 'validation_failed',
          errors: { capacity: ['Capacity must be between 1 and 1000 seats.'], title: ['Too short.', 'Second'] },
        },
      }),
    );

    expect(problem.status).toBe(400);
    expect(problem.code).toBe('validation_failed');
    expect(fieldErrors(problem)).toEqual({ capacity: 'Capacity must be between 1 and 1000 seats.', title: 'Too short.' });
  });

  it('turns a network failure into a readable message', () => {
    const problem = toProblem(new HttpErrorResponse({ status: 0 }));

    expect(problem.code).toBe('network_error');
    expect(problem.detail).toContain('could not be reached');
  });

  it('falls back to the status when the body is not a problem', () => {
    const problem = toProblem(new HttpErrorResponse({ status: 502, statusText: 'Bad Gateway', error: '<html>' }));

    expect(problem.code).toBe('http_502');
    expect(problem.errors).toEqual({});
  });

  it('only retries failures that a retry can fix', () => {
    expect(isRetryable(new HttpErrorResponse({ status: 0 }))).toBe(true);
    expect(isRetryable(new HttpErrorResponse({ status: 503 }))).toBe(true);
    expect(isRetryable(new HttpErrorResponse({ status: 409 }))).toBe(false);
    expect(isRetryable(new Error('boom'))).toBe(false);
  });
});
