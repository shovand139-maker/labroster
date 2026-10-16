import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { FormControl, FormGroup } from '@angular/forms';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { LabEvent, Paged } from '../../core/models';
import { EnrollPanelComponent } from './enroll-panel.component';
import { EventListPage, SEARCH_DEBOUNCE_MS } from './event-list.page';
import { endsAfterStart } from './event-form.page';

const sampleEvent: LabEvent = {
  id: 'e1',
  title: 'AZ-104 Bootcamp, Cohort 12',
  templateId: 't1',
  templateTitle: 'AZ-104 Administrator Lab',
  cloud: 'azure',
  creditsPerSeat: 15,
  startsAt: '2026-10-22T09:00:00Z',
  endsAt: '2026-10-22T17:00:00Z',
  capacity: 12,
  enrolled: 9,
  waitlisted: 2,
  seatsLeft: 3,
  status: 'open',
  createdBy: 'seed',
  createdAt: '2026-10-01T09:00:00Z',
  updatedAt: '2026-10-01T09:00:00Z',
  eTag: '"AAAAAAAAB9E="',
};

function page(items: LabEvent[]): Paged<LabEvent> {
  return { items, page: 1, pageSize: 12, total: items.length };
}

function configure() {
  TestBed.configureTestingModule({
    providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
  });
  return TestBed.inject(HttpTestingController);
}

afterEach(() => vi.useRealTimers());

describe('EventListPage', () => {
  it('shows each event with its seat count and waitlist', async () => {
    const backend = configure();
    const fixture = TestBed.createComponent(EventListPage);
    fixture.detectChanges();

    backend.expectOne((r) => r.url === '/api/events').flush(page([sampleEvent]));
    await fixture.whenStable();

    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(text).toContain('AZ-104 Bootcamp, Cohort 12');
    expect(text).toContain('9/12 seats');
    expect(text).toContain('2 waitlisted');
    backend.verify();
  });

  it('waits for the user to stop typing and searches once', async () => {
    vi.useFakeTimers();
    const backend = configure();
    const fixture = TestBed.createComponent(EventListPage);
    fixture.detectChanges();
    backend.expectOne((r) => r.url === '/api/events').flush(page([sampleEvent]));

    const input = (fixture.nativeElement as HTMLElement).querySelector<HTMLInputElement>('[data-testid="event-search"]')!;
    for (const value of ['k', 'ku', 'kub', 'kube']) {
      input.value = value;
      input.dispatchEvent(new Event('input'));
      vi.advanceTimersByTime(SEARCH_DEBOUNCE_MS - 50);
    }
    backend.expectNone((r) => r.url === '/api/events');

    vi.advanceTimersByTime(SEARCH_DEBOUNCE_MS);

    const requests = backend.match((r) => r.url === '/api/events');
    expect(requests.length).toBe(1);
    expect(requests[0].request.params.get('search')).toBe('kube');
    requests[0].flush(page([]));
    backend.verify();
  });
});

describe('EnrollPanelComponent', () => {
  it('retries a failed enrollment with the same idempotency key', async () => {
    vi.useFakeTimers();
    const backend = configure();
    const fixture = TestBed.createComponent(EnrollPanelComponent);
    fixture.componentRef.setInput('eventId', 'e1');
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    const fill = (selector: string, value: string) => {
      const field = root.querySelector<HTMLInputElement>(selector)!;
      field.value = value;
      field.dispatchEvent(new Event('input'));
    };
    fill('#enroll-name', 'Asha Rao');
    fill('#enroll-email', 'asha@example.com');
    root.querySelector('form')!.dispatchEvent(new Event('submit'));

    const first = backend.expectOne('/api/events/e1/enrollments');
    const key = first.request.headers.get('Idempotency-Key');
    first.flush(null, { status: 503, statusText: 'Service Unavailable' });

    vi.advanceTimersByTime(2_000);
    const retry = backend.expectOne('/api/events/e1/enrollments');
    expect(retry.request.headers.get('Idempotency-Key')).toBe(key);
    retry.flush({ id: 'n1', fullName: 'Asha Rao', status: 'waitlisted', waitlistPosition: 3 });

    await fixture.whenStable();
    expect(root.querySelector('[data-testid="enroll-notice"]')?.textContent).toContain('waitlist at position 3');
    backend.verify();
  });

  it('does not retry a request the server rejected on purpose', () => {
    vi.useFakeTimers();
    const backend = configure();
    const fixture = TestBed.createComponent(EnrollPanelComponent);
    fixture.componentRef.setInput('eventId', 'e1');
    fixture.detectChanges();

    const root = fixture.nativeElement as HTMLElement;
    for (const [selector, value] of [['#enroll-name', 'Asha Rao'], ['#enroll-email', 'asha@example.com']]) {
      const field = root.querySelector<HTMLInputElement>(selector)!;
      field.value = value;
      field.dispatchEvent(new Event('input'));
    }
    root.querySelector('form')!.dispatchEvent(new Event('submit'));

    backend
      .expectOne('/api/events/e1/enrollments')
      .flush({ status: 409, code: 'already_enrolled', detail: 'Already on the roster.' }, { status: 409, statusText: 'Conflict' });
    vi.advanceTimersByTime(5_000);

    backend.expectNone('/api/events/e1/enrollments');
    backend.verify();
  });
});

describe('endsAfterStart', () => {
  const group = (startsAt: string, endsAt: string) =>
    new FormGroup({ startsAt: new FormControl(startsAt), endsAt: new FormControl(endsAt) });

  it('accepts an event that ends after it starts', () => {
    expect(endsAfterStart(group('2026-10-22T09:00', '2026-10-22T17:00'))).toBeNull();
  });

  it('rejects an event that ends before or when it starts', () => {
    expect(endsAfterStart(group('2026-10-22T09:00', '2026-10-22T09:00'))).toEqual({ endsBeforeStart: true });
    expect(endsAfterStart(group('2026-10-22T09:00', '2026-10-21T17:00'))).toEqual({ endsBeforeStart: true });
  });
});
