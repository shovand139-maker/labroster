import { Component, inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import { catchError, map, of, startWith } from 'rxjs';
import { ApiService } from '../../core/api.service';
import { formatDateTime } from '../../core/format';
import { Credits } from '../../core/models';
import { ApiProblem, toProblem } from '../../core/problem';

type CreditsState = { kind: 'loading' } | { kind: 'loaded'; credits: Credits } | { kind: 'error'; problem: ApiProblem };

const REASON_LABEL: Record<string, string> = {
  allocation: 'Credits added',
  enrollment: 'Seat booked',
  refund: 'Seat released',
};

@Component({
  selector: 'app-credits',
  imports: [RouterLink],
  template: `
    <div class="page-head">
      <div>
        <h1>Credits</h1>
        <p class="muted">Every seat is paid for from this balance. Cancelled seats are refunded automatically.</p>
      </div>
    </div>

    @switch (state().kind) {
      @case ('loading') {
        <div class="card skeleton"></div>
      }
      @case ('error') {
        <p class="alert alert-error" role="alert">{{ problem().detail }}</p>
      }
      @case ('loaded') {
        @let c = credits();
        <dl class="stat-cards">
          <div class="card"><dt>Balance</dt><dd data-testid="credits-balance">{{ c.balance }}</dd></div>
          <div class="card"><dt>Spent, last 30 days</dt><dd>{{ c.spent30Days }}</dd></div>
          <div class="card"><dt>Refunded, last 30 days</dt><dd>{{ c.refunded30Days }}</dd></div>
          <div class="card"><dt>Allocated in total</dt><dd>{{ c.allocated }}</dd></div>
        </dl>

        <section class="card">
          <h2>Latest movements</h2>
          <div class="table-wrap">
            <table class="table">
              <thead>
                <tr><th scope="col">When</th><th scope="col">What</th><th scope="col">Event</th><th scope="col" class="num">Credits</th></tr>
              </thead>
              <tbody>
                @for (entry of c.recent; track entry.id) {
                  <tr>
                    <td class="small">{{ when(entry.createdAt) }}</td>
                    <td>{{ reason(entry.reason) }}</td>
                    <td>
                      @if (entry.eventId) {
                        <a [routerLink]="['/events', entry.eventId]">{{ entry.eventTitle }}</a>
                      } @else {
                        <span class="muted">—</span>
                      }
                    </td>
                    <td class="num" [class.positive]="entry.delta > 0">{{ entry.delta > 0 ? '+' : '' }}{{ entry.delta }}</td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
        </section>
      }
    }
  `,
})
export class CreditsPage {
  private readonly api = inject(ApiService);

  protected readonly state = toSignal(
    this.api.credits().pipe(
      map((credits): CreditsState => ({ kind: 'loaded', credits })),
      catchError((error: unknown) => of<CreditsState>({ kind: 'error', problem: toProblem(error) })),
      startWith<CreditsState>({ kind: 'loading' }),
    ),
    { requireSync: true },
  );

  protected credits(): Credits {
    const state = this.state();
    return state.kind === 'loaded' ? state.credits : { balance: 0, allocated: 0, spent30Days: 0, refunded30Days: 0, recent: [] };
  }

  protected problem(): ApiProblem {
    const state = this.state();
    return state.kind === 'error' ? state.problem : toProblem(null);
  }

  protected reason(value: string): string {
    return REASON_LABEL[value] ?? value;
  }

  protected when(value: string): string {
    return formatDateTime(value);
  }
}
