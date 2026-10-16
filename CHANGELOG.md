# Changelog

## 1.0.0

First release.

**For instructors**
- Create events from lab templates and move them through draft, open, closed and cancelled.
- Enroll learners one at a time, or paste a CSV export and see an outcome for every row.
- Full events keep a waitlist. A freed seat goes to the next person in line automatically.
- Editing an event someone else has just changed shows their version instead of overwriting it.

**For admins**
- Credit balance, 30-day spend and refunds, and the latest ledger movements.
- An audit log of every change, newest first.

**For operators**
- Organizations are isolated by row-level security in SQL Server. The app's database login can only execute the `api` procedures.
- Deploys to Azure App Service and Azure SQL with managed identity and Entra-only authentication. No passwords in any environment.
- `/health/live` and `/health/ready`, plus Application Insights through OpenTelemetry.
