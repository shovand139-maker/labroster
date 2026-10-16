import { EnrollmentStatus, EventStatus } from './models';

const dateTime = new Intl.DateTimeFormat(undefined, {
  weekday: 'short',
  day: 'numeric',
  month: 'short',
  hour: '2-digit',
  minute: '2-digit',
});
const time = new Intl.DateTimeFormat(undefined, { hour: '2-digit', minute: '2-digit' });

export function formatWhen(startsAt: string, endsAt: string): string {
  const start = new Date(startsAt);
  const end = new Date(endsAt);
  const sameDay = start.toDateString() === end.toDateString();
  return sameDay ? `${dateTime.format(start)} – ${time.format(end)}` : `${dateTime.format(start)} – ${dateTime.format(end)}`;
}

export function formatDateTime(value: string): string {
  return dateTime.format(new Date(value));
}

/** Value for an <input type="datetime-local"> in the browser's time zone. */
export function toLocalInput(iso: string): string {
  const date = new Date(iso);
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}T${pad(date.getHours())}:${pad(date.getMinutes())}`;
}

/** An <input type="datetime-local"> value, read in the browser's time zone, as ISO 8601 UTC. */
export function fromLocalInput(value: string): string {
  return new Date(value).toISOString();
}

export const EVENT_STATUS_LABEL: Record<EventStatus, string> = {
  draft: 'Draft',
  open: 'Open',
  closed: 'Closed',
  cancelled: 'Cancelled',
};

export const ENROLLMENT_STATUS_LABEL: Record<EnrollmentStatus, string> = {
  enrolled: 'Enrolled',
  waitlisted: 'Waitlisted',
  cancelled: 'Cancelled',
};

export const BULK_OUTCOME_LABEL: Record<string, string> = {
  enrolled: 'Enrolled',
  waitlisted: 'Waitlisted',
  already_enrolled: 'Already on roster',
  duplicate_in_file: 'Duplicate in file',
  invalid_email: 'Invalid email',
  invalid_name: 'Name missing',
  insufficient_credits: 'Not enough credits',
};
