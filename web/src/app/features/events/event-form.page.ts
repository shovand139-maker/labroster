import { Component, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { AbstractControl, FormBuilder, ReactiveFormsModule, ValidationErrors, Validators } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { catchError, of } from 'rxjs';
import { ApiService } from '../../core/api.service';
import { fromLocalInput, toLocalInput } from '../../core/format';
import { LabTemplate } from '../../core/models';
import { ApiProblem, fieldErrors, toProblem } from '../../core/problem';

/** Cross-field check mirrored from the API: the event must end after it starts. */
export function endsAfterStart(group: AbstractControl): ValidationErrors | null {
  const startsAt = group.get('startsAt')?.value as string | undefined;
  const endsAt = group.get('endsAt')?.value as string | undefined;
  return startsAt && endsAt && new Date(endsAt) <= new Date(startsAt) ? { endsBeforeStart: true } : null;
}

@Component({
  selector: 'app-event-form',
  imports: [ReactiveFormsModule, RouterLink],
  template: `
    <a routerLink="/events" class="back">← Events</a>
    <div class="page-head">
      <div>
        <h1>New event</h1>
        <p class="muted">Events start as drafts. Open one when you are ready to take enrollments.</p>
      </div>
    </div>

    <form class="card form" [formGroup]="form" (ngSubmit)="submit()" novalidate>
      <div class="field">
        <label for="title">Title</label>
        <input id="title" formControlName="title" placeholder="AZ-104 Bootcamp, Cohort 13" autocomplete="off" />
        @if (error('title'); as message) {
          <p class="field-error">{{ message }}</p>
        }
      </div>

      <div class="field">
        <label for="templateId">Lab template</label>
        <select id="templateId" formControlName="templateId">
          <option value="" disabled>Choose a lab</option>
          @for (template of templates(); track template.id) {
            <option [value]="template.id">
              {{ template.title }} · {{ template.durationMinutes }} min · {{ template.creditsPerSeat }} credits per seat
            </option>
          }
        </select>
        @if (error('templateId'); as message) {
          <p class="field-error">{{ message }}</p>
        }
      </div>

      <div class="field-row">
        <div class="field">
          <label for="startsAt">Starts</label>
          <input id="startsAt" type="datetime-local" formControlName="startsAt" />
          @if (error('startsAt'); as message) {
            <p class="field-error">{{ message }}</p>
          }
        </div>
        <div class="field">
          <label for="endsAt">Ends</label>
          <input id="endsAt" type="datetime-local" formControlName="endsAt" />
          @if (error('endsAt'); as message) {
            <p class="field-error">{{ message }}</p>
          }
        </div>
        <div class="field field-narrow">
          <label for="capacity">Seats</label>
          <input id="capacity" type="number" min="1" max="1000" formControlName="capacity" />
          @if (error('capacity'); as message) {
            <p class="field-error">{{ message }}</p>
          }
        </div>
      </div>

      @if (problem(); as p) {
        <p class="alert alert-error" role="alert">{{ p.detail }}</p>
      }

      <div class="form-actions">
        <a routerLink="/events" class="btn btn-ghost">Cancel</a>
        <button type="submit" class="btn btn-primary" [disabled]="saving()">
          {{ saving() ? 'Creating…' : 'Create draft event' }}
        </button>
      </div>
    </form>
  `,
})
export class EventFormPage {
  private readonly api = inject(ApiService);
  private readonly router = inject(Router);
  private readonly fb = inject(FormBuilder);

  protected readonly templates = toSignal(this.api.templates().pipe(catchError(() => of<LabTemplate[]>([]))), {
    initialValue: [],
  });
  protected readonly saving = signal(false);
  protected readonly problem = signal<ApiProblem | null>(null);
  private readonly serverErrors = signal<Record<string, string>>({});

  protected readonly form = this.fb.nonNullable.group(
    {
      title: ['', [Validators.required, Validators.minLength(3), Validators.maxLength(160)]],
      templateId: ['', Validators.required],
      startsAt: [toLocalInput(defaultStart().toISOString()), Validators.required],
      endsAt: [toLocalInput(new Date(defaultStart().getTime() + 6 * 3_600_000).toISOString()), Validators.required],
      capacity: [20, [Validators.required, Validators.min(1), Validators.max(1000)]],
    },
    { validators: endsAfterStart },
  );

  protected error(field: 'title' | 'templateId' | 'startsAt' | 'endsAt' | 'capacity'): string | null {
    const server = this.serverErrors()[field];
    if (server) {
      return server;
    }

    const control = this.form.controls[field];
    if (!control.touched && !this.submitted()) {
      return null;
    }
    if (control.hasError('required')) {
      return 'Required.';
    }
    if (control.hasError('minlength')) {
      return 'At least 3 characters.';
    }
    if (control.hasError('min') || control.hasError('max')) {
      return 'Between 1 and 1000 seats.';
    }
    if (field === 'endsAt' && this.form.hasError('endsBeforeStart')) {
      return 'Must be after the start time.';
    }
    return null;
  }

  private readonly submitted = signal(false);

  protected submit(): void {
    this.submitted.set(true);
    this.serverErrors.set({});
    this.problem.set(null);
    this.form.markAllAsTouched();
    if (this.form.invalid) {
      return;
    }

    const value = this.form.getRawValue();
    this.saving.set(true);
    this.api
      .createEvent({
        title: value.title.trim(),
        templateId: value.templateId,
        startsAt: fromLocalInput(value.startsAt),
        endsAt: fromLocalInput(value.endsAt),
        capacity: Number(value.capacity),
      })
      .subscribe({
        next: (created) => void this.router.navigate(['/events', created.id]),
        error: (error: unknown) => {
          const problem = toProblem(error);
          this.saving.set(false);
          this.serverErrors.set(fieldErrors(problem));
          this.problem.set(Object.keys(problem.errors).length ? null : problem);
        },
      });
  }
}

function defaultStart(): Date {
  const date = new Date();
  date.setDate(date.getDate() + 7);
  date.setHours(9, 0, 0, 0);
  return date;
}
