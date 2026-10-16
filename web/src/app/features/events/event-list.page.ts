import { AsyncPipe } from '@angular/common';
import { Component, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import {
  BehaviorSubject,
  Observable,
  catchError,
  combineLatest,
  debounceTime,
  distinctUntilChanged,
  map,
  of,
  shareReplay,
  skip,
  startWith,
  switchMap,
} from 'rxjs';
import { ApiService, EventQuery } from '../../core/api.service';
import { AuthService } from '../../core/auth.service';
import { EVENT_STATUS_LABEL, formatWhen } from '../../core/format';
import { EventStatus, LabEvent, Paged } from '../../core/models';
import { ApiProblem, toProblem } from '../../core/problem';

export const SEARCH_DEBOUNCE_MS = 300;
const PAGE_SIZE = 12;

type ListState =
  | { kind: 'loading' }
  | { kind: 'loaded'; page: Paged<LabEvent> }
  | { kind: 'error'; problem: ApiProblem };

@Component({
  selector: 'app-event-list',
  imports: [AsyncPipe, RouterLink],
  template: `
    <div class="page-head">
      <div>
        <h1>Events</h1>
        <p class="muted">Instructor-led lab events in {{ auth.me()?.tenantName }}, in date order.</p>
      </div>
      @if (auth.canManageEvents()) {
        <a routerLink="/events/new" class="btn btn-primary">New event</a>
      }
    </div>

    <div class="toolbar">
      <label class="search">
        <span class="sr-only">Search events</span>
        <input
          type="search"
          placeholder="Search by title"
          data-testid="event-search"
          [value]="search$.value"
          (input)="onSearch($any($event.target).value)"
        />
      </label>
      <div class="chips" role="group" aria-label="Filter by status">
        @for (option of statusOptions; track option.value) {
          <button
            type="button"
            class="chip"
            [class.chip-on]="status$.value === option.value"
            [attr.aria-pressed]="status$.value === option.value"
            (click)="onStatus(option.value)"
          >
            {{ option.label }}
          </button>
        }
      </div>
    </div>

    @if (state$ | async; as state) {
      @switch (state.kind) {
        @case ('loading') {
          <div class="event-grid" aria-busy="true">
            @for (i of [1, 2, 3]; track i) {
              <div class="card skeleton"></div>
            }
          </div>
        }
        @case ('error') {
          <p class="alert alert-error" role="alert">{{ errorOf(state).detail }}</p>
        }
        @case ('loaded') {
          @let page = pageOf(state);
          @if (page.items.length === 0) {
            <div class="empty card">
              <h2>No events match</h2>
              <p class="muted">Try a different search or status.</p>
            </div>
          } @else {
            <ul class="event-grid" data-testid="event-list">
              @for (item of page.items; track item.id) {
                <li>
                  <a class="card event-card" [routerLink]="['/events', item.id]">
                    <div class="event-card-top">
                      <span class="status status-{{ item.status }}">{{ statusLabel[item.status] }}</span>
                      <span class="cloud cloud-{{ item.cloud }}">{{ item.cloud }}</span>
                    </div>
                    <h2 class="event-title">{{ item.title }}</h2>
                    <p class="muted small">{{ item.templateTitle }}</p>
                    <p class="when">{{ when(item) }}</p>
                    <div class="seats" [attr.aria-label]="item.enrolled + ' of ' + item.capacity + ' seats taken'">
                      <div class="seat-bar"><span [style.width.%]="fill(item)"></span></div>
                      <span class="small">
                        <strong>{{ item.enrolled }}</strong>/{{ item.capacity }} seats
                        @if (item.waitlisted > 0) {
                          · <span class="waitlist-count">{{ item.waitlisted }} waitlisted</span>
                        }
                      </span>
                    </div>
                  </a>
                </li>
              }
            </ul>
            @if (page.total > page.pageSize) {
              <nav class="pager" aria-label="Pages">
                <button type="button" class="btn btn-ghost" [disabled]="page.page <= 1" (click)="goTo(page.page - 1)">
                  Previous
                </button>
                <span class="muted small">Page {{ page.page }} of {{ lastPage(page) }}</span>
                <button
                  type="button"
                  class="btn btn-ghost"
                  [disabled]="page.page >= lastPage(page)"
                  (click)="goTo(page.page + 1)"
                >
                  Next
                </button>
              </nav>
            }
          }
        }
      }
    }
  `,
})
export class EventListPage {
  private readonly api = inject(ApiService);
  protected readonly auth = inject(AuthService);

  protected readonly statusLabel = EVENT_STATUS_LABEL;
  protected readonly statusOptions: { value: EventStatus | ''; label: string }[] = [
    { value: '', label: 'All' },
    { value: 'open', label: 'Open' },
    { value: 'draft', label: 'Draft' },
    { value: 'closed', label: 'Closed' },
    { value: 'cancelled', label: 'Cancelled' },
  ];

  protected readonly search$ = new BehaviorSubject('');
  protected readonly status$ = new BehaviorSubject<EventStatus | ''>('');
  private readonly page$ = new BehaviorSubject(1);

  /**
   * Typing is debounced and de-duplicated; switchMap cancels the request for a search
   * the user has already typed past, so results never arrive out of order.
   */
  protected readonly state$: Observable<ListState> = combineLatest([
    this.search$.pipe(
      skip(1),
      debounceTime(SEARCH_DEBOUNCE_MS),
      startWith(this.search$.value),
      map((s) => s.trim()),
      distinctUntilChanged(),
    ),
    this.status$.pipe(distinctUntilChanged()),
    this.page$.pipe(distinctUntilChanged()),
  ]).pipe(
    map(([search, status, page]): EventQuery => ({ search, status, page, pageSize: PAGE_SIZE })),
    switchMap((query) =>
      this.api.listEvents(query).pipe(
        map((page): ListState => ({ kind: 'loaded', page })),
        catchError((error: unknown) => of<ListState>({ kind: 'error', problem: toProblem(error) })),
        startWith<ListState>({ kind: 'loading' }),
      ),
    ),
    shareReplay({ bufferSize: 1, refCount: true }),
  );

  protected onSearch(value: string): void {
    this.page$.next(1);
    this.search$.next(value);
  }

  protected onStatus(value: EventStatus | ''): void {
    this.page$.next(1);
    this.status$.next(value);
  }

  protected goTo(page: number): void {
    this.page$.next(page);
  }

  protected when(item: LabEvent): string {
    return formatWhen(item.startsAt, item.endsAt);
  }

  protected fill(item: LabEvent): number {
    return Math.min(100, Math.round((item.enrolled / item.capacity) * 100));
  }

  protected lastPage(page: Paged<LabEvent>): number {
    return Math.max(1, Math.ceil(page.total / page.pageSize));
  }

  protected pageOf(state: ListState): Paged<LabEvent> {
    return state.kind === 'loaded' ? state.page : { items: [], page: 1, pageSize: PAGE_SIZE, total: 0 };
  }

  protected errorOf(state: ListState): ApiProblem {
    return state.kind === 'error' ? state.problem : toProblem(null);
  }
}
