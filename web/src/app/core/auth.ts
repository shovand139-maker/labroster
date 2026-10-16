import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { catchError, map, throwError } from 'rxjs';
import { AuthService } from './auth.service';

/** Adds the bearer token to API calls and ends the session when the API rejects it. */
export const authInterceptor: HttpInterceptorFn = (request, next) => {
  const auth = inject(AuthService);
  const token = auth.token();
  const isApi = request.url.startsWith('/api/');
  const isSignIn = request.url.startsWith('/api/dev/');

  const outgoing =
    token && isApi && !isSignIn ? request.clone({ setHeaders: { Authorization: `Bearer ${token}` } }) : request;

  return next(outgoing).pipe(
    catchError((error: unknown) => {
      if (error instanceof HttpErrorResponse && error.status === 401 && isApi && !isSignIn) {
        auth.signOut();
      }
      return throwError(() => error);
    }),
  );
};

export const signedInGuard: CanActivateFn = () => {
  const auth = inject(AuthService);
  const router = inject(Router);
  return auth.restore().pipe(map((ok) => (ok ? true : router.createUrlTree(['/sign-in']))));
};

export const auditGuard: CanActivateFn = () => {
  const auth = inject(AuthService);
  const router = inject(Router);
  return auth.restore().pipe(map((ok) => (ok && auth.canReadAudit() ? true : router.createUrlTree(['/events']))));
};
