# AGENTS.md

Working agreement for anyone changing this repository. Read it before writing code. Each rule is here because breaking it is easy and expensive to catch in review.

## Commands

| Task | Command |
|---|---|
| SQL Server for local work | `docker compose up -d` |
| Migrate and seed | see "Run it locally" in `README.md` |
| Build | `dotnet build LabRoster.slnx` (warnings are errors) |
| .NET tests | `LABROSTER_TEST_SQL=… dotnet test LabRoster.slnx` |
| Angular tests | `cd web && npm test` |
| Angular build | `cd web && npm run build` |
| End-to-end | API on :5080 serving `web/dist`, then `cd web && npx playwright test` |

Integration tests are skipped, not passed, when `LABROSTER_TEST_SQL` is missing. A change to SQL or data access is not verified until they have run.

## Database rules

1. **Tenant isolation lives in the database.** Every new tenant-owned table gets a `TenantId uniqueidentifier NOT NULL` column and, in the same migration, `ALTER SECURITY POLICY security.TenantIsolation` adding a FILTER predicate and BLOCK predicates for AFTER INSERT and AFTER UPDATE. The integration test `Every_table_with_a_tenant_id_is_covered_by_row_level_security` fails if one is missed.
2. **Procedures never take a tenant or user parameter.** They read `SESSION_CONTEXT(N'TenantId')` and `SESSION_CONTEXT(N'UserId')`. A `@TenantId` parameter on an `api.*` procedure fails review.
3. **The app executes procedures and nothing else.** Never grant `SELECT`, `INSERT`, `UPDATE` or `DELETE` to `app_executor`. New procedures the API calls go in the `api` schema; operator procedures go in `admin`.
4. **Validate, then return.** A procedure raises every error before its first result set. An error after a result set is easy for the client to miss.
5. **Error format:** `THROW 50xxx, N'snake_case_code|Human sentence.', 1`. 50400–50499 become that HTTP status. Pick the status the API would return, and reuse existing codes where they fit.
6. **Lock order:** event row (`UPDLOCK, HOLDLOCK`), then enrollments, then `TenantCredits`. Any procedure that touches seats or credits follows it.
7. **Credits only move as a pair:** a `CreditLedger` row and the matching `TenantCredits.Balance` change, in the same transaction.
8. **Migrations are immutable.** Never edit an applied `V###__` file; the migrator refuses to run if one changed. Add `V(next)__description.sql`. Views, functions and procedures live in `R__` files as `CREATE OR ALTER`.

## API rules

- Get the caller with `CurrentUser.From(principal)`. Do not read claims anywhere else.
- Open connections with `ITenantConnectionFactory`, never `new SqlConnection` in a store.
- A write that changes an event requires `If-Match`; use `EventEndpoints.TryReadIfMatch`.
- Validate request bodies in the API (all field errors at once, as `ApiProblem.Validation`) **and** keep the procedure's own checks.
- Every endpoint is authenticated by default. Opting out with `AllowAnonymous` needs a comment saying why.

## Angular rules

- Components talk to the API only through `ApiService`.
- Streams that react to input use `switchMap` so stale responses are dropped; searches are debounced.
- Retries reuse the same `Idempotency-Key` and only retry `isRetryable` errors.
- Show API problems with `toProblem`; map field errors back onto the form with `fieldErrors`.
- Add a `data-testid` only where a role or label cannot identify the element.

## Definition of done

1. The spec's acceptance criteria each map to a named test (see `docs/specs/`).
2. The new test fails without the change. Check it by reverting the change once.
3. CI is green: build, .NET unit and integration tests, Angular tests and build, Playwright.
4. The PR description follows `.github/pull_request_template.md`, including rollback notes.
5. `CHANGELOG.md` has a line for anything a user or operator would notice.

## When a mistake repeats

If the same review comment comes up twice, add a rule here and, where possible, a test that enforces it.
