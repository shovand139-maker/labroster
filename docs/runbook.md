# LabRoster runbook

For whoever is on call. Commands assume the Azure CLI is signed in to the right subscription.

## Health

- `GET /health/live` answers when the process is up. `GET /health/ready` also opens a database connection; App Service uses it as the health check and takes failing instances out of rotation.
- Application Insights receives requests, SQL dependency calls, exceptions and logs through OpenTelemetry. Every problem response carries a `traceId`; search for it under Transaction search.

## Deploy and roll back

- Deploy: run the **Deploy** workflow for `qa`, check the app, then run it for `production` (it waits for an approver).
- Roll back the code: re-run the Deploy workflow from the last good run. Migrations are forward-only and additive, so older code runs against the newer schema.
- Never roll back a migration by editing it. Write a new `V###__` script that undoes the change.

## Alerts and what to check

### 5xx rate above 1% for 5 minutes

```kusto
requests
| where timestamp > ago(30m) and toint(resultCode) >= 500
| summarize count() by operation_Name, resultCode
| order by count_ desc
```

Then look at the matching exceptions:

```kusto
exceptions
| where timestamp > ago(30m)
| summarize count() by type, outerMessage
| order by count_ desc
```

- `SqlException` 40613 or 40501: Azure SQL is reconfiguring or throttling. It clears by itself. If it keeps happening, check DTU or vCore use on the database.
- `SqlException` 18456 (login failed): the web app's managed identity lost its database user. Re-run the Deploy workflow, whose migrate step re-creates the user and role membership.

### Many 503s with code `deadlock_victim`

The procedures take locks in a fixed order, so deadlocks point to a new code path that breaks it. Look at the Deadlocks diagnostic log in Log Analytics (`AzureDiagnostics | where Category == "Deadlocks"`) and compare the procedures in the graph against AGENTS.md rule 6.

### Spike in 412 `stale_etag`

Two people are editing the same event, or a client keeps sending an old ETag. A short burst is normal. A steady stream from one user usually means a stale browser tab or an integration that caches ETags.

### Customer says their credit balance is wrong

The ledger is the source of truth. This must return zero rows:

```sql
-- Run as a SQL admin. Set the tenant first; row-level security applies to admins too.
EXEC sys.sp_set_session_context @key = N'TenantId', @value = '<tenant id>';

SELECT c.Balance AS cached, SUM(l.Delta) AS ledger
FROM dbo.TenantCredits AS c
JOIN dbo.CreditLedger AS l ON l.TenantId = c.TenantId
GROUP BY c.TenantId, c.Balance
HAVING c.Balance <> SUM(l.Delta);
```

If it returns a row, do not edit the balance by hand. Capture the ledger and the audit log for the period and raise an incident.

## Onboarding a customer organization

```sql
EXEC admin.usp_Tenant_Provision
    @TenantId = '<their Entra directory id>', @Name = N'<name>', @Slug = '<slug>', @InitialCredits = 500;
EXEC admin.usp_Template_Upsert
    @TenantId = '<their Entra directory id>', @TemplateId = NEWID(), @Title = N'<lab>',
    @Cloud = 'azure', @DurationMinutes = 120, @CreditsPerSeat = 15;
```

Until this runs, users from that directory can sign in but `GET /api/me` answers `403 tenant_not_onboarded`.
