import { Component, inject } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { AuthService } from './core/auth.service';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet, RouterLink, RouterLinkActive],
  template: `
    @if (auth.me(); as me) {
      <header class="topbar">
        <div class="topbar-inner">
          <a routerLink="/events" class="brand" aria-label="LabRoster home">
            <span class="brand-mark" aria-hidden="true">LR</span>
            <span class="brand-name">LabRoster</span>
          </a>
          <nav class="nav" aria-label="Main">
            <a routerLink="/events" routerLinkActive="active">Events</a>
            <a routerLink="/credits" routerLinkActive="active">Credits</a>
            @if (auth.canReadAudit()) {
              <a routerLink="/audit" routerLinkActive="active">Audit log</a>
            }
          </nav>
          <div class="who">
            <span class="org" data-testid="tenant-name">{{ me.tenantName }}</span>
            <a routerLink="/credits" class="credit-pill" data-testid="credit-balance" title="Credit balance">
              {{ me.creditBalance }} credits
            </a>
            <span class="user">
              <span class="user-name">{{ me.name }}</span>
              <span class="user-role">{{ me.role }}</span>
            </span>
            <button type="button" class="btn btn-ghost btn-sm" (click)="auth.signOut()">Sign out</button>
          </div>
        </div>
      </header>
    }
    <main class="page">
      <router-outlet />
    </main>
  `,
})
export class App {
  protected readonly auth = inject(AuthService);
}
