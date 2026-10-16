import { HttpClient, HttpHeaders, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, map } from 'rxjs';
import {
  AuditEntry,
  BulkResult,
  CancelResult,
  Credits,
  DevToken,
  DevUser,
  Enrollment,
  EnrollmentStatus,
  EventInput,
  EventStatus,
  LabEvent,
  LabTemplate,
  Me,
  Paged,
} from './models';

export interface EventQuery {
  search: string;
  status: EventStatus | '';
  page: number;
  pageSize: number;
}

export interface RosterQuery {
  search: string;
  status: EnrollmentStatus | '';
  page: number;
  pageSize: number;
}

export interface EnrollResponse {
  enrollment: Enrollment;
  replayed: boolean;
}

/** Typed access to the LabRoster API. Every write that changes an event carries its ETag. */
@Injectable({ providedIn: 'root' })
export class ApiService {
  private readonly http = inject(HttpClient);

  me(): Observable<Me> {
    return this.http.get<Me>('/api/me');
  }

  templates(): Observable<LabTemplate[]> {
    return this.http.get<LabTemplate[]>('/api/templates');
  }

  credits(): Observable<Credits> {
    return this.http.get<Credits>('/api/tenant/credits');
  }

  audit(page: number, pageSize = 25): Observable<Paged<AuditEntry>> {
    return this.http.get<Paged<AuditEntry>>('/api/audit', { params: { page, pageSize } });
  }

  listEvents(query: EventQuery): Observable<Paged<LabEvent>> {
    return this.http.get<Paged<LabEvent>>('/api/events', { params: toParams(query) });
  }

  getEvent(id: string): Observable<LabEvent> {
    return this.http.get<LabEvent>(`/api/events/${id}`);
  }

  createEvent(input: EventInput): Observable<LabEvent> {
    return this.http.post<LabEvent>('/api/events', input);
  }

  updateEvent(id: string, input: EventInput, etag: string): Observable<LabEvent> {
    return this.http.put<LabEvent>(`/api/events/${id}`, input, { headers: ifMatch(etag) });
  }

  setStatus(id: string, status: Exclude<EventStatus, 'draft'>, etag: string): Observable<LabEvent> {
    return this.http.post<LabEvent>(`/api/events/${id}/status`, { status }, { headers: ifMatch(etag) });
  }

  roster(eventId: string, query: RosterQuery): Observable<Paged<Enrollment>> {
    return this.http.get<Paged<Enrollment>>(`/api/events/${eventId}/roster`, { params: toParams(query) });
  }

  /**
   * The idempotency key must be created once per user action and reused for retries,
   * so a request that timed out but succeeded is never applied twice.
   */
  enroll(eventId: string, email: string, fullName: string, idempotencyKey: string): Observable<EnrollResponse> {
    return this.http
      .post<Enrollment>(
        `/api/events/${eventId}/enrollments`,
        { email, fullName },
        { headers: { 'Idempotency-Key': idempotencyKey }, observe: 'response' },
      )
      .pipe(
        map((response) => ({
          enrollment: response.body as Enrollment,
          replayed: response.headers.get('Idempotent-Replay') === 'true',
        })),
      );
  }

  bulkEnroll(eventId: string, csv: string): Observable<BulkResult> {
    return this.http.post<BulkResult>(`/api/events/${eventId}/enrollments/bulk`, csv, {
      headers: { 'Content-Type': 'text/csv' },
    });
  }

  cancelEnrollment(enrollmentId: string): Observable<CancelResult> {
    return this.http.post<CancelResult>(`/api/enrollments/${enrollmentId}/cancel`, null);
  }

  devUsers(): Observable<DevUser[]> {
    return this.http.get<DevUser[]>('/api/dev/users');
  }

  devToken(userId: string): Observable<DevToken> {
    return this.http.post<DevToken>('/api/dev/token', { userId });
  }
}

function ifMatch(etag: string): HttpHeaders {
  return new HttpHeaders({ 'If-Match': etag });
}

function toParams(query: object): HttpParams {
  let params = new HttpParams();
  for (const [key, value] of Object.entries(query)) {
    if (value !== '' && value !== null && value !== undefined) {
      params = params.set(key, String(value));
    }
  }
  return params;
}
