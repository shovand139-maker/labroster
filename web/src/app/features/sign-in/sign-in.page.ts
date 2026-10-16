import { Component, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { Router } from '@angular/router';
import { catchError, map, of, startWith } from 'rxjs';
import { ApiService } from '../../core/api.service';
import { AuthService } from '../../core/auth.service';
import { DevUser } from '../../core/models';
import { ApiProblem, toProblem } from '../../core/problem';

type UsersState = { status: 'loading' } | { status: 'ready'; users: DevUser[] } | { status: 'unavailable' };

@Component({
  selector: 'app-sign-in',
  template: `
    <section class="sign-in">
      <div class="sign-in-intro">
        <p class="eyebrow">Instructor-led cloud labs</p>
        <h1>Run every lab event from one roster.</h1>
        <p class="lede">
          Open seats, enroll learners one by one or from a spreadsheet, and let the waitlist fill freed seats
          automatically. Every seat is paid for from your organization's credit balance, and nothing one
          organization does is visible to another.
        </p>
      </div>

      <div class="card sign-in-card">
        <h2>Sign in</h2>
        @switch (users().status) {
          @case ('loading') {
            <p class="muted">Loading accounts…</p>
          }
          @case ('unavailable') {
            <p>Sign in with your Microsoft work account. Development sign-in is turned off on this server.</p>
          }
          @case ('ready') {
            <p class="muted small">Local demo accounts. Each one is a different role or organization.</p>
            <ul class="account-list">
              @for (user of readyUsers(); track user.id) {
                <li>
                  <button type="button" class="account" [disabled]="busy() !== null" (click)="signIn(user)">
                    <span class="avatar" aria-hidden="true">{{ initials(user.name) }}</span>
                    <span class="account-text">
                      <span class="account-name">{{ user.name }}</span>
                      <span class="muted small">{{ user.tenantName }}</span>
                    </span>
                    <span class="role-chip role-{{ user.role.toLowerCase() }}">{{ user.role }}</span>
                  </button>
                </li>
              }
            </ul>
          }
        }
        @if (error(); as problem) {
          <p class="alert alert-error" role="alert">{{ problem.detail }}</p>
        }
      </div>
    </section>
  `,
})
export class SignInPage {
  private readonly api = inject(ApiService);
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);

  protected readonly busy = signal<string | null>(null);
  protected readonly error = signal<ApiProblem | null>(null);

  protected readonly users = toSignal(
    this.api.devUsers().pipe(
      map((users): UsersState => ({ status: 'ready', users })),
      catchError(() => of<UsersState>({ status: 'unavailable' })),
      startWith<UsersState>({ status: 'loading' }),
    ),
    { requireSync: true },
  );

  protected readyUsers(): DevUser[] {
    const state = this.users();
    return state.status === 'ready' ? state.users : [];
  }

  protected initials(name: string): string {
    return name
      .split(/\s+/)
      .map((part) => part[0])
      .slice(0, 2)
      .join('')
      .toUpperCase();
  }

  protected signIn(user: DevUser): void {
    this.busy.set(user.id);
    this.error.set(null);
    this.auth.signIn(user.id).subscribe({
      next: () => void this.router.navigate(['/events']),
      error: (error: unknown) => {
        this.busy.set(null);
        this.error.set(toProblem(error));
      },
    });
  }
}
