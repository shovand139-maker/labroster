import { AsyncPipe } from '@angular/common';
import { Component, inject, input, output, signal } from '@angular/core';
import { toObservable } from '@angular/core/rxjs-interop';
import {
  BehaviorSubject,
  Observable,
  catchError,
  combineLatest,
  debounceTime,
  distinctUntilChanged,
  filter,
  interval,
  map,
  of,
  skip,
  startWith,
  switchMap,
} from 'rxjs';
import { ApiService } from '../../core/api.service';
import { ENROLLMENT_STATUS_LABEL, formatDateTime } from '../../core/format';
import { Enrollment, EnrollmentStatus, Paged } from '../../core/models';
import { ApiProblem, toProblem } from '../../core/problem';

export const ROSTER_POLL_MS = 15_000;

type RosterState =
  | { kind: 'loading' }
  | { kind: 'loaded'; page: Paged<Enrollment> }
  | { kind: 'error'; problem: ApiProblem };

@Component({
  selector: 'app-roster',
  imports: [AsyncPipe],
  template: `
    <section class="card roster" aria-labelledby="roster-heading">
      <div class="section-head">
        <h2 id="roster-heading">Roster</h2>
        <div class="toolbar toolbar-tight">
          <label class="search">
            <span class="sr-only">Search the roster</span>
            <input
              type="search"
              placeholder="Name or email"
              data-testid="roster-search"
              (input)="search$.next($any($event.target).value)"
            />
          </label>
          <select aria-label="Filter by status" (change)="status$.next($any($event.target).value)">
            <option value="">Everyone</option>
            <option value="enrolled">Enrolled</option>
            <option value="waitlisted">Waitlisted</option>
            <option value="cancelled">Cancelled</option>
          </select>
        </div>
      </div>

      @if (actionError(); as problem) {
        <p class="alert alert-error" role="alert">{{ problem.detail }}</p>
      }

      @if (state$ | async; as state) {
        @if (state.kind === 'error') {
          <p class="alert alert-error" role="alert">{{ problemOf(state).detail }}</p>
        } @else if (state.kind === 'loading') {
          <p class="muted">Loading roster…</p>
        } @else {
          @let page = pageOf(state);
          @if (page.items.length === 0) {
            <p class="muted">Nobody is on this roster yet.</p>
          } @else {
            <div class="table-wrap">
              <table class="table" data-testid="roster-table">
                <thead>
                  <tr>
                    <th scope="col">Learner</th>
                    <th scope="col">Status</th>
                    <th scope="col" class="num">Credits</th>
                    <th scope="col">Since</th>
                    @if (canManage()) {
                      <th scope="col"><span class="sr-only">Actions</span></th>
                    }
                  </tr>
                </thead>
                <tbody>
                  @for (row of page.items; track row.id) {
                    <tr [attr.data-email]="row.email">
                      <td>
                        <div class="learner">{{ row.fullName }}</div>
                        <div class="muted small">{{ row.email }}</div>
                      </td>
                      <td>
                        <span class="status status-{{ row.status }}" data-testid="enrollment-status">
                          {{ statusLabel[row.status] }}
                          @if (row.waitlistPosition) {
                            #{{ row.waitlistPosition }}
                          }
                        </span>
                      </td>
                      <td class="num">{{ row.creditsCharged }}</td>
                      <td class="small">{{ since(row) }}</td>
                      @if (canManage()) {
                        <td class="row-actions">
                          @if (row.status !== 'cancelled') {
                            @if (confirming() === row.id) {
                              <button type="button" class="btn btn-danger btn-sm" (click)="cancel(row)" [disabled]="busy()">
                                Confirm cancel
                              </button>
                              <button type="button" class="btn btn-ghost btn-sm" (click)="confirming.set(null)">Keep</button>
                            } @else {
                              <button type="button" class="btn btn-ghost btn-sm" (click)="confirming.set(row.id)">
                                Cancel seat
                              </button>
                            }
                          }
                        </td>
                      }
                    </tr>
                  }
                </tbody>
              </table>
            </div>
            <p class="muted small">{{ page.total }} {{ page.total === 1 ? 'person' : 'people' }} on this roster.</p>
          }
        }
      }
    </section>
  `,
})
export class RosterComponent {
  private readonly api = inject(ApiService);

  readonly eventId = input.required<string>();
  readonly version = input(0);
  readonly canManage = input(false);
  readonly changed = output<string>();

  protected readonly statusLabel = ENROLLMENT_STATUS_LABEL;
  protected readonly search$ = new BehaviorSubject('');
  protected readonly status$ = new BehaviorSubject<EnrollmentStatus | ''>('');
  protected readonly confirming = signal<string | null>(null);
  protected readonly busy = signal(false);
  protected readonly actionError = signal<ApiProblem | null>(null);

  /** Reloads on search, filter, parent changes, and every 15 s while the tab is visible. */
  protected readonly state$: Observable<RosterState> = combineLatest([
    toObservable(this.eventId),
    this.search$.pipe(skip(1), debounceTime(300), startWith(''), map((s) => s.trim()), distinctUntilChanged()),
    this.status$.pipe(distinctUntilChanged()),
    toObservable(this.version),
    interval(ROSTER_POLL_MS).pipe(
      filter(() => typeof document === 'undefined' || !document.hidden),
      startWith(0),
    ),
  ]).pipe(
    switchMap(([eventId, search, status], index) =>
      this.api.roster(eventId, { search, status, page: 1, pageSize: 200 }).pipe(
        map((page): RosterState => ({ kind: 'loaded', page })),
        catchError((error: unknown) => of<RosterState>({ kind: 'error', problem: toProblem(error) })),
        // Only the first load shows a spinner; refreshes swap the table in place.
        index === 0 ? startWith<RosterState>({ kind: 'loading' }) : (source: Observable<RosterState>) => source,
      ),
    ),
  );

  protected cancel(row: Enrollment): void {
    this.busy.set(true);
    this.actionError.set(null);
    this.api.cancelEnrollment(row.id).subscribe({
      next: () => {
        this.busy.set(false);
        this.confirming.set(null);
        this.changed.emit(row.id);
      },
      error: (error: unknown) => {
        this.busy.set(false);
        this.actionError.set(toProblem(error));
      },
    });
  }

  protected since(row: Enrollment): string {
    return formatDateTime(row.statusChangedAt);
  }

  protected pageOf(state: RosterState): Paged<Enrollment> {
    return state.kind === 'loaded' ? state.page : { items: [], page: 1, pageSize: 200, total: 0 };
  }

  protected problemOf(state: RosterState): ApiProblem {
    return state.kind === 'error' ? state.problem : toProblem(null);
  }
}
