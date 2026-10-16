import { Injectable, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { Observable, catchError, map, of, switchMap, tap } from 'rxjs';
import { ApiService } from './api.service';
import { Me } from './models';

const TOKEN_KEY = 'labroster.token';

/**
 * Holds the signed-in user. Locally the token comes from the API's development
 * sign-in; in a deployment MSAL would supply an Entra ID token instead, and the
 * rest of the app would not change because both carry the same claims.
 */
@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly api = inject(ApiService);
  private readonly router = inject(Router);

  private readonly tokenSignal = signal<string | null>(readToken());
  readonly me = signal<Me | null>(null);
  readonly isSignedIn = computed(() => this.me() !== null);
  readonly canManageEvents = computed(() => this.me()?.permissions.manageEvents ?? false);
  readonly canReadAudit = computed(() => this.me()?.permissions.readAudit ?? false);

  token(): string | null {
    return this.tokenSignal();
  }

  signIn(userId: string): Observable<Me> {
    return this.api.devToken(userId).pipe(
      tap((issued) => this.storeToken(issued.accessToken)),
      switchMap(() => this.api.me()),
      tap((me) => this.me.set(me)),
    );
  }

  /** Restores the session after a reload. Resolves to false when there is no usable token. */
  restore(): Observable<boolean> {
    if (this.me()) {
      return of(true);
    }
    if (!this.tokenSignal()) {
      return of(false);
    }
    return this.api.me().pipe(
      tap((me) => this.me.set(me)),
      map(() => true),
      catchError(() => {
        this.clear();
        return of(false);
      }),
    );
  }

  /** Refreshes the header's credit balance after enrollments change it. */
  refreshMe(): void {
    if (this.tokenSignal()) {
      this.api.me().subscribe({ next: (me) => this.me.set(me), error: () => undefined });
    }
  }

  signOut(): void {
    this.clear();
    void this.router.navigate(['/sign-in']);
  }

  clear(): void {
    this.storeToken(null);
    this.me.set(null);
  }

  private storeToken(token: string | null): void {
    this.tokenSignal.set(token);
    try {
      if (token) {
        sessionStorage.setItem(TOKEN_KEY, token);
      } else {
        sessionStorage.removeItem(TOKEN_KEY);
      }
    } catch {
      // Storage can be unavailable (private mode); the session then lasts until reload.
    }
  }
}

function readToken(): string | null {
  try {
    return sessionStorage.getItem(TOKEN_KEY);
  } catch {
    return null;
  }
}
