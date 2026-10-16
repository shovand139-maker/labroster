import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { ApiService } from './api.service';
import { authInterceptor } from './auth';
import { AuthService } from './auth.service';

function setup(token: string | null) {
  sessionStorage.clear();
  if (token) {
    sessionStorage.setItem('labroster.token', token);
  }
  TestBed.configureTestingModule({
    providers: [provideHttpClient(withInterceptors([authInterceptor])), provideHttpClientTesting(), provideRouter([])],
  });
  return {
    http: TestBed.inject(HttpClient),
    api: TestBed.inject(ApiService),
    auth: TestBed.inject(AuthService),
    backend: TestBed.inject(HttpTestingController),
  };
}

afterEach(() => TestBed.inject(HttpTestingController).verify());

describe('authInterceptor', () => {
  it('sends the bearer token to the API', () => {
    const { http, backend } = setup('token-123');

    http.get('/api/events').subscribe();

    expect(backend.expectOne('/api/events').request.headers.get('Authorization')).toBe('Bearer token-123');
  });

  it('never sends the token to development sign-in or other hosts', () => {
    const { http, backend } = setup('token-123');

    http.post('/api/dev/token', {}).subscribe();
    http.get('https://cdn.example.com/file.json').subscribe();

    expect(backend.expectOne('/api/dev/token').request.headers.has('Authorization')).toBe(false);
    expect(backend.expectOne('https://cdn.example.com/file.json').request.headers.has('Authorization')).toBe(false);
  });

  it('ends the session when the API rejects the token', () => {
    const { http, auth, backend } = setup('expired');
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);

    http.get('/api/me').subscribe({ error: () => undefined });
    backend.expectOne('/api/me').flush({ status: 401 }, { status: 401, statusText: 'Unauthorized' });

    expect(auth.token()).toBeNull();
    expect(sessionStorage.getItem('labroster.token')).toBeNull();
    expect(navigate).toHaveBeenCalledWith(['/sign-in']);
  });
});

describe('ApiService', () => {
  it('sends the event ETag in If-Match when changing status', () => {
    const { api, backend } = setup('t');

    api.setStatus('e1', 'open', '"AAAAAAAAB9E="').subscribe();

    const request = backend.expectOne('/api/events/e1/status');
    expect(request.request.method).toBe('POST');
    expect(request.request.headers.get('If-Match')).toBe('"AAAAAAAAB9E="');
    expect(request.request.body).toEqual({ status: 'open' });
  });

  it('sends the idempotency key and reports a replayed enrollment', () => {
    const { api, backend } = setup('t');
    let replayed: boolean | undefined;

    api.enroll('e1', 'asha@example.com', 'Asha Rao', 'key-0001').subscribe((r) => (replayed = r.replayed));

    const request = backend.expectOne('/api/events/e1/enrollments');
    expect(request.request.headers.get('Idempotency-Key')).toBe('key-0001');
    request.flush({ id: 'n1', status: 'enrolled' }, { headers: { 'Idempotent-Replay': 'true' } });
    expect(replayed).toBe(true);
  });

  it('uploads a roster as text/csv', () => {
    const { api, backend } = setup('t');

    api.bulkEnroll('e1', 'email,fullName\na@example.com,A\n').subscribe();

    const request = backend.expectOne('/api/events/e1/enrollments/bulk');
    expect(request.request.headers.get('Content-Type')).toBe('text/csv');
    expect(request.request.body).toContain('a@example.com');
  });

  it('leaves empty filters out of the query string', () => {
    const { api, backend } = setup('t');

    api.listEvents({ search: '', status: 'open', page: 2, pageSize: 12 }).subscribe();

    const request = backend.expectOne((r) => r.url === '/api/events');
    expect(request.request.params.keys().sort()).toEqual(['page', 'pageSize', 'status']);
    expect(request.request.params.get('status')).toBe('open');
  });
});
