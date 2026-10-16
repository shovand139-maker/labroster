import { Component, computed, effect, inject, input, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { AuthService } from '../../core/auth.service';
import { ApiService } from '../../core/api.service';
import { EVENT_STATUS_LABEL, formatDateTime, formatWhen, fromLocalInput, toLocalInput } from '../../core/format';
import { EventStatus, LabEvent } from '../../core/models';
import { ApiProblem, fieldErrors, toProblem } from '../../core/problem';
import { EnrollPanelComponent } from './enroll-panel.component';
import { endsAfterStart } from './event-form.page';
import { RosterComponent } from './roster.component';

type Transition = { to: Exclude<EventStatus, 'draft'>; label: string; danger?: boolean };

const TRANSITIONS: Record<EventStatus, Transition[]> = {
  draft: [{ to: 'open', label: 'Open for enrollment' }, { to: 'cancelled', label: 'Cancel event', danger: true }],
  open: [{ to: 'closed', label: 'Close enrollment' }, { to: 'cancelled', label: 'Cancel event', danger: true }],
  closed: [{ to: 'open', label: 'Reopen' }, { to: 'cancelled', label: 'Cancel event', danger: true }],
  cancelled: [],
};

@Component({
  selector: 'app-event-detail',
  imports: [ReactiveFormsModule, RouterLink, RosterComponent, EnrollPanelComponent],
  template: `
    <a routerLink="/events" class="back">← Events</a>

    @if (loadError(); as problem) {
      <div class="card empty">
        <h1>{{ problem.status === 404 ? 'Event not found' : 'This event could not be loaded' }}</h1>
        <p class="muted">{{ problem.detail }}</p>
      </div>
    } @else if (event(); as item) {
      <header class="event-head">
        <div class="event-head-main">
          <div class="row-gap">
            <span class="status status-{{ item.status }}" data-testid="event-status">{{ statusLabel[item.status] }}</span>
            <span class="cloud cloud-{{ item.cloud }}">{{ item.cloud }}</span>
          </div>
          <h1 data-testid="event-title">{{ item.title }}</h1>
          <p class="muted">{{ item.templateTitle }} · {{ when(item) }}</p>
        </div>
        <dl class="stats">
          <div><dt>Seats taken</dt><dd data-testid="seats-taken">{{ item.enrolled }}/{{ item.capacity }}</dd></div>
          <div><dt>Waitlist</dt><dd data-testid="waitlist-size">{{ item.waitlisted }}</dd></div>
          <div><dt>Per seat</dt><dd>{{ item.creditsPerSeat }} cr</dd></div>
        </dl>
      </header>

      <div class="seat-bar seat-bar-lg" aria-hidden="true"><span [style.width.%]="fill(item)"></span></div>

      @if (notice(); as message) {
        <p class="alert alert-warn" role="status">{{ message }}</p>
      }
      @if (actionError(); as problem) {
        <p class="alert alert-error" role="alert">{{ problem.detail }}</p>
      }

      @if (auth.canManageEvents() && transitions().length) {
        <div class="actions">
          @for (t of transitions(); track t.to) {
            @if (t.danger && confirmingCancel()) {
              <span class="confirm">
                Cancel the event and refund every seat?
                <button type="button" class="btn btn-danger btn-sm" [disabled]="busy()" (click)="setStatus(t)">Yes, cancel event</button>
                <button type="button" class="btn btn-ghost btn-sm" (click)="confirmingCancel.set(false)">Keep it</button>
              </span>
            } @else {
              <button
                type="button"
                class="btn"
                [class.btn-primary]="!t.danger"
                [class.btn-ghost-danger]="t.danger"
                [disabled]="busy()"
                (click)="t.danger ? confirmingCancel.set(true) : setStatus(t)"
              >
                {{ t.label }}
              </button>
            }
          }
          @if (item.status !== 'cancelled') {
            <button type="button" class="btn btn-ghost" (click)="toggleEdit(item)">{{ editing() ? 'Close editor' : 'Edit details' }}</button>
          }
        </div>
      }

      @if (editing()) {
        <form class="card form" [formGroup]="edit" (ngSubmit)="save(item)" novalidate>
          <h2>Edit event</h2>
          <div class="field">
            <label for="edit-title">Title</label>
            <input id="edit-title" formControlName="title" />
            @if (editErrors()['title']; as m) { <p class="field-error">{{ m }}</p> }
          </div>
          <div class="field-row">
            <div class="field">
              <label for="edit-start">Starts</label>
              <input id="edit-start" type="datetime-local" formControlName="startsAt" />
            </div>
            <div class="field">
              <label for="edit-end">Ends</label>
              <input id="edit-end" type="datetime-local" formControlName="endsAt" />
              @if (editErrors()['endsAt']; as m) { <p class="field-error">{{ m }}</p> }
            </div>
            <div class="field field-narrow">
              <label for="edit-capacity">Seats</label>
              <input id="edit-capacity" type="number" min="1" max="1000" formControlName="capacity" />
              @if (editErrors()['capacity']; as m) { <p class="field-error">{{ m }}</p> }
            </div>
          </div>
          <div class="form-actions">
            <button type="submit" class="btn btn-primary" [disabled]="busy() || edit.invalid">Save changes</button>
          </div>
        </form>
      }

      @if (auth.canManageEvents() && item.status === 'open') {
        <app-enroll-panel [eventId]="item.id" (changed)="onRosterChanged()" />
      } @else if (auth.canManageEvents() && item.status === 'draft') {
        <p class="muted">Open the event to start enrolling learners.</p>
      }

      <app-roster [eventId]="item.id" [version]="rosterVersion()" [canManage]="auth.canManageEvents()" (changed)="onRosterChanged()" />

      <p class="muted small">Created by {{ item.createdBy }} · last changed {{ changed(item) }}</p>
    } @else {
      <div class="card skeleton skeleton-tall" aria-busy="true"></div>
    }
  `,
})
export class EventDetailPage {
  private readonly api = inject(ApiService);
  private readonly fb = inject(FormBuilder);
  protected readonly auth = inject(AuthService);

  /** Bound from the :id route parameter. */
  readonly id = input.required<string>();

  protected readonly event = signal<LabEvent | null>(null);
  protected readonly loadError = signal<ApiProblem | null>(null);
  protected readonly actionError = signal<ApiProblem | null>(null);
  protected readonly notice = signal<string | null>(null);
  protected readonly busy = signal(false);
  protected readonly editing = signal(false);
  protected readonly confirmingCancel = signal(false);
  protected readonly editErrors = signal<Record<string, string>>({});
  protected readonly rosterVersion = signal(0);
  protected readonly statusLabel = EVENT_STATUS_LABEL;

  protected readonly transitions = computed(() => {
    const item = this.event();
    return item ? TRANSITIONS[item.status] : [];
  });

  protected readonly edit = this.fb.nonNullable.group(
    {
      title: ['', [Validators.required, Validators.minLength(3)]],
      startsAt: ['', Validators.required],
      endsAt: ['', Validators.required],
      capacity: [1, [Validators.required, Validators.min(1), Validators.max(1000)]],
    },
    { validators: endsAfterStart },
  );

  constructor() {
    effect(() => this.load(this.id()));
  }

  protected setStatus(transition: Transition): void {
    const item = this.event();
    if (!item) {
      return;
    }
    this.run(this.api.setStatus(item.id, transition.to, item.eTag), () => {
      this.confirmingCancel.set(false);
      this.rosterVersion.update((v) => v + 1);
      this.auth.refreshMe();
    });
  }

  protected toggleEdit(item: LabEvent): void {
    this.editing.update((open) => !open);
    this.editErrors.set({});
    this.edit.reset({
      title: item.title,
      startsAt: toLocalInput(item.startsAt),
      endsAt: toLocalInput(item.endsAt),
      capacity: item.capacity,
    });
  }

  protected save(item: LabEvent): void {
    const value = this.edit.getRawValue();
    this.editErrors.set({});
    this.run(
      this.api.updateEvent(
        item.id,
        {
          title: value.title.trim(),
          startsAt: fromLocalInput(value.startsAt),
          endsAt: fromLocalInput(value.endsAt),
          capacity: Number(value.capacity),
        },
        item.eTag,
      ),
      () => {
        this.editing.set(false);
        this.rosterVersion.update((v) => v + 1);
        this.auth.refreshMe();
      },
    );
  }

  protected onRosterChanged(): void {
    this.rosterVersion.update((v) => v + 1);
    this.auth.refreshMe();
    this.load(this.id(), true);
  }

  protected when(item: LabEvent): string {
    return formatWhen(item.startsAt, item.endsAt);
  }

  protected changed(item: LabEvent): string {
    return formatDateTime(item.updatedAt);
  }

  protected fill(item: LabEvent): number {
    return Math.min(100, Math.round((item.enrolled / item.capacity) * 100));
  }

  private load(id: string, quiet = false): void {
    if (!quiet) {
      this.loadError.set(null);
    }
    this.api.getEvent(id).subscribe({
      next: (item) => this.event.set(item),
      error: (error: unknown) => this.loadError.set(toProblem(error)),
    });
  }

  /** Runs a write. A 412 means someone else changed the event first: reload and explain. */
  private run(request: ReturnType<ApiService['getEvent']>, onSuccess: () => void): void {
    this.busy.set(true);
    this.actionError.set(null);
    this.notice.set(null);
    request.subscribe({
      next: (updated) => {
        this.busy.set(false);
        this.event.set(updated);
        onSuccess();
      },
      error: (error: unknown) => {
        const problem = toProblem(error);
        this.busy.set(false);
        if (problem.status === 412) {
          this.notice.set('Someone else changed this event while you were looking at it. It now shows their version, so check it and try again.');
          this.editing.set(false);
          this.load(this.id(), true);
          return;
        }
        const fields = fieldErrors(problem);
        if (Object.keys(fields).length) {
          this.editErrors.set(fields);
        } else {
          this.actionError.set(problem);
        }
      },
    });
  }
}
