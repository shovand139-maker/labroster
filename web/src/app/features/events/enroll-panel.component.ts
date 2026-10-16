import { Component, inject, input, output, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { retry, throwError, timer } from 'rxjs';
import { ApiService } from '../../core/api.service';
import { BULK_OUTCOME_LABEL } from '../../core/format';
import { BulkResult } from '../../core/models';
import { ApiProblem, fieldErrors, isRetryable, toProblem } from '../../core/problem';

export function newIdempotencyKey(): string {
  if (typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function') {
    return crypto.randomUUID();
  }
  const bytes = new Uint8Array(16);
  crypto.getRandomValues(bytes);
  return Array.from(bytes, (b) => b.toString(16).padStart(2, '0')).join('');
}

const SAMPLE_CSV = 'email,fullName\nasha.rao@example.com,Asha Rao\nben.ortiz@example.com,Ben Ortiz\n';

@Component({
  selector: 'app-enroll-panel',
  imports: [ReactiveFormsModule],
  template: `
    <section class="card" aria-labelledby="enroll-heading">
      <div class="section-head">
        <h2 id="enroll-heading">Enroll learners</h2>
        <div class="tabs" role="tablist">
          <button type="button" role="tab" class="tab" [class.tab-on]="mode() === 'one'" [attr.aria-selected]="mode() === 'one'" (click)="mode.set('one')">
            One learner
          </button>
          <button type="button" role="tab" class="tab" [class.tab-on]="mode() === 'csv'" [attr.aria-selected]="mode() === 'csv'" (click)="mode.set('csv')">
            From a spreadsheet
          </button>
        </div>
      </div>

      @if (mode() === 'one') {
        <form class="inline-form" [formGroup]="single" (ngSubmit)="enrollOne()" novalidate>
          <div class="field">
            <label for="enroll-name">Full name</label>
            <input id="enroll-name" formControlName="fullName" autocomplete="off" />
            @if (errors()['fullName']; as message) {
              <p class="field-error">{{ message }}</p>
            }
          </div>
          <div class="field">
            <label for="enroll-email">Email</label>
            <input id="enroll-email" type="email" formControlName="email" autocomplete="off" />
            @if (errors()['email']; as message) {
              <p class="field-error">{{ message }}</p>
            }
          </div>
          <button type="submit" class="btn btn-primary" [disabled]="busy()">{{ busy() ? 'Enrolling…' : 'Enroll' }}</button>
        </form>
      } @else {
        <div class="csv">
          <label for="csv-text">Paste a CSV export with email and fullName columns, or choose a file.</label>
          <textarea id="csv-text" rows="6" spellcheck="false" [value]="csv()" (input)="csv.set($any($event.target).value)"></textarea>
          <div class="csv-actions">
            <label class="btn btn-ghost btn-file">
              Choose file
              <input type="file" accept=".csv,text/csv" (change)="readFile($event)" />
            </label>
            <button type="button" class="btn btn-primary" [disabled]="busy() || !csv().trim()" (click)="enrollCsv()">
              {{ busy() ? 'Uploading…' : 'Enroll everyone' }}
            </button>
          </div>
        </div>
      }

      @if (notice(); as message) {
        <p class="alert alert-ok" role="status" data-testid="enroll-notice">{{ message }}</p>
      }
      @if (problem(); as p) {
        <p class="alert alert-error" role="alert">{{ p.detail }}</p>
      }

      @if (bulk(); as result) {
        <div class="bulk-summary" data-testid="bulk-summary">
          <p>
            <strong>{{ result.enrolled }}</strong> enrolled ·
            <strong>{{ result.waitlisted }}</strong> waitlisted ·
            <strong>{{ result.skipped }}</strong> skipped
          </p>
          @if (result.skipped > 0) {
            <ul class="bulk-rows">
              @for (row of skippedRows(result); track row.row) {
                <li>Row {{ row.row + 1 }} · {{ row.email || '(no email)' }} · {{ outcomeLabel(row.outcome) }}</li>
              }
            </ul>
          }
        </div>
      }
    </section>
  `,
})
export class EnrollPanelComponent {
  private readonly api = inject(ApiService);
  private readonly fb = inject(FormBuilder);

  readonly eventId = input.required<string>();
  readonly changed = output<void>();

  protected readonly mode = signal<'one' | 'csv'>('one');
  protected readonly busy = signal(false);
  protected readonly notice = signal<string | null>(null);
  protected readonly problem = signal<ApiProblem | null>(null);
  protected readonly errors = signal<Record<string, string>>({});
  protected readonly csv = signal(SAMPLE_CSV);
  protected readonly bulk = signal<BulkResult | null>(null);

  protected readonly single = this.fb.nonNullable.group({
    fullName: ['', [Validators.required, Validators.minLength(2)]],
    email: ['', [Validators.required, Validators.email]],
  });

  protected enrollOne(): void {
    this.reset();
    if (this.single.invalid) {
      this.errors.set({
        ...(this.single.controls.fullName.invalid ? { fullName: 'Enter the learner’s name.' } : {}),
        ...(this.single.controls.email.invalid ? { email: 'Enter a valid email address.' } : {}),
      });
      return;
    }

    const { fullName, email } = this.single.getRawValue();
    // One key per click: retries of this request reuse it, so a timeout never double-books.
    const key = newIdempotencyKey();
    this.busy.set(true);
    this.api
      .enroll(this.eventId(), email, fullName, key)
      .pipe(
        retry({
          count: 2,
          delay: (error: unknown, attempt) => (isRetryable(error) ? timer(400 * 2 ** attempt) : throwError(() => error)),
        }),
      )
      .subscribe({
        next: ({ enrollment }) => {
          this.busy.set(false);
          this.single.reset();
          this.notice.set(
            enrollment.status === 'enrolled'
              ? `${enrollment.fullName} is enrolled.`
              : `${enrollment.fullName} is on the waitlist at position ${enrollment.waitlistPosition}.`,
          );
          this.changed.emit();
        },
        error: (error: unknown) => this.fail(error),
      });
  }

  protected enrollCsv(): void {
    this.reset();
    this.busy.set(true);
    this.api.bulkEnroll(this.eventId(), this.csv()).subscribe({
      next: (result) => {
        this.busy.set(false);
        this.bulk.set(result);
        this.changed.emit();
      },
      error: (error: unknown) => this.fail(error),
    });
  }

  protected readFile(event: Event): void {
    const file = (event.target as HTMLInputElement).files?.[0];
    if (file) {
      void file.text().then((text) => this.csv.set(text));
    }
  }

  protected skippedRows(result: BulkResult) {
    return result.rows.filter((r) => r.outcome !== 'enrolled' && r.outcome !== 'waitlisted');
  }

  protected outcomeLabel(outcome: string): string {
    return BULK_OUTCOME_LABEL[outcome] ?? outcome;
  }

  private reset(): void {
    this.notice.set(null);
    this.problem.set(null);
    this.errors.set({});
    this.bulk.set(null);
  }

  private fail(error: unknown): void {
    const problem = toProblem(error);
    this.busy.set(false);
    const fields = fieldErrors(problem);
    this.errors.set(fields);
    this.problem.set(Object.keys(fields).length ? null : problem);
  }
}
