## What changed

<!-- One or two sentences a reviewer can check the diff against. Link the spec. -->

## Why

## How it was tested

- [ ] Unit tests
- [ ] Integration tests against SQL Server (`LABROSTER_TEST_SQL` set)
- [ ] Playwright, if the change is visible in the app
- [ ] A test fails without this change

## Review checklist

- [ ] Every new tenant-owned table has `TenantId` and row-level security predicates
- [ ] Procedures read tenant and user from `SESSION_CONTEXT`, never from parameters
- [ ] Writes that change an event check its ETag
- [ ] No new grants to `app_executor` beyond `EXECUTE ON SCHEMA::api`
- [ ] Errors use `THROW 50xxx, N'code|message'` and the code is documented

## Rollout and rollback

<!-- Migrations are forward-only. Say what happens if this release is rolled back. -->
