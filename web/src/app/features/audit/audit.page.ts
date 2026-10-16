import { AsyncPipe, JsonPipe } from '@angular/common';
import { Component, inject } from '@angular/core';
import { BehaviorSubject, catchError, map, of, startWith, switchMap } from 'rxjs';
import { ApiService } from '../../core/api.service';
import { formatDateTime } from '../../core/format';
import { AuditEntry, Paged } from '../../core/models';
import { ApiProblem, toProblem } from '../../core/problem';

type AuditState = { kind: 'loading' } | { kind: 'loaded'; page: Paged<AuditEntry> } | { kind: 'error'; problem: ApiProblem };

@Component({
  selector: 'app-audit',
  imports: [AsyncPipe, JsonPipe],
  template: `
    <div class="page-head">
      <div>
        <h1>Audit log</h1>
        <p class="muted">Every change in your organization, newest first. Written by the database in the same transaction as the change.</p>
      </div>
    </div>

    @if (state$ | async; as state) {
      @if (state.kind === 'error') {
        <p class="alert alert-error" role="alert">{{ problemOf(state).detail }}</p>
      } @else if (state.kind === 'loading') {
        <div class="card skeleton"></div>
      } @else {
        @let page = pageOf(state);
        <section class="card">
          <div class="table-wrap">
            <table class="table" data-testid="audit-table">
              <thead>
                <tr><th scope="col">When</th><th scope="col">Who</th><th scope="col">Action</th><th scope="col">Details</th></tr>
              </thead>
              <tbody>
                @for (entry of page.items; track entry.id) {
                  <tr>
                    <td class="small">{{ when(entry.createdAt) }}</td>
                    <td>{{ entry.actor }}</td>
                    <td><code>{{ entry.action }}</code></td>
                    <td class="small details">{{ entry.details | json }}</td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
          <nav class="pager" aria-label="Pages">
            <button type="button" class="btn btn-ghost" [disabled]="page.page <= 1" (click)="page$.next(page.page - 1)">Newer</button>
            <span class="muted small">Page {{ page.page }}</span>
            <button type="button" class="btn btn-ghost" [disabled]="page.page * page.pageSize >= page.total" (click)="page$.next(page.page + 1)">
              Older
            </button>
          </nav>
        </section>
      }
    }
  `,
})
export class AuditPage {
  private readonly api = inject(ApiService);
  protected readonly page$ = new BehaviorSubject(1);

  protected readonly state$ = this.page$.pipe(
    switchMap((page) =>
      this.api.audit(page).pipe(
        map((result): AuditState => ({ kind: 'loaded', page: result })),
        catchError((error: unknown) => of<AuditState>({ kind: 'error', problem: toProblem(error) })),
        startWith<AuditState>({ kind: 'loading' }),
      ),
    ),
  );

  protected when(value: string): string {
    return formatDateTime(value);
  }

  protected pageOf(state: AuditState): Paged<AuditEntry> {
    return state.kind === 'loaded' ? state.page : { items: [], page: 1, pageSize: 25, total: 0 };
  }

  protected problemOf(state: AuditState): ApiProblem {
    return state.kind === 'error' ? state.problem : toProblem(null);
  }
}
