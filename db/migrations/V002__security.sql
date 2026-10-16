-- Security model
--   api       procedures the web API may call. The only thing granted to the app.
--   admin     provisioning procedures for operators and the migrator. Never granted to the app.
--   security  the row-level security predicate and policy.
--
-- The API sets SESSION_CONTEXT('TenantId') read-only on every connection it opens.
-- Row-level security filters every read and blocks every write to another tenant,
-- including writes from inside procedures and from db_owner.

CREATE SCHEMA api AUTHORIZATION dbo;
GO
CREATE SCHEMA admin AUTHORIZATION dbo;
GO
CREATE SCHEMA security AUTHORIZATION dbo;
GO

CREATE FUNCTION security.fn_TenantAccess (@TenantId uniqueidentifier)
RETURNS TABLE
WITH SCHEMABINDING
AS
RETURN
    SELECT 1 AS AccessGranted
    WHERE @TenantId = CAST(SESSION_CONTEXT(N'TenantId') AS uniqueidentifier);
GO

CREATE SECURITY POLICY security.TenantIsolation
    ADD FILTER PREDICATE security.fn_TenantAccess(TenantId) ON dbo.Tenants,
    ADD FILTER PREDICATE security.fn_TenantAccess(TenantId) ON dbo.TenantCredits,
    ADD BLOCK  PREDICATE security.fn_TenantAccess(TenantId) ON dbo.TenantCredits AFTER INSERT,
    ADD BLOCK  PREDICATE security.fn_TenantAccess(TenantId) ON dbo.TenantCredits AFTER UPDATE,
    ADD FILTER PREDICATE security.fn_TenantAccess(TenantId) ON dbo.LabTemplates,
    ADD BLOCK  PREDICATE security.fn_TenantAccess(TenantId) ON dbo.LabTemplates AFTER INSERT,
    ADD BLOCK  PREDICATE security.fn_TenantAccess(TenantId) ON dbo.LabTemplates AFTER UPDATE,
    ADD FILTER PREDICATE security.fn_TenantAccess(TenantId) ON dbo.Events,
    ADD BLOCK  PREDICATE security.fn_TenantAccess(TenantId) ON dbo.Events AFTER INSERT,
    ADD BLOCK  PREDICATE security.fn_TenantAccess(TenantId) ON dbo.Events AFTER UPDATE,
    ADD FILTER PREDICATE security.fn_TenantAccess(TenantId) ON dbo.Enrollments,
    ADD BLOCK  PREDICATE security.fn_TenantAccess(TenantId) ON dbo.Enrollments AFTER INSERT,
    ADD BLOCK  PREDICATE security.fn_TenantAccess(TenantId) ON dbo.Enrollments AFTER UPDATE,
    ADD FILTER PREDICATE security.fn_TenantAccess(TenantId) ON dbo.CreditLedger,
    ADD BLOCK  PREDICATE security.fn_TenantAccess(TenantId) ON dbo.CreditLedger AFTER INSERT,
    ADD FILTER PREDICATE security.fn_TenantAccess(TenantId) ON dbo.AuditLog,
    ADD BLOCK  PREDICATE security.fn_TenantAccess(TenantId) ON dbo.AuditLog AFTER INSERT
WITH (STATE = ON, SCHEMABINDING = ON);
GO

-- Bulk enrollment input, passed as a table-valued parameter.
CREATE TYPE api.LearnerList AS TABLE
(
    RowNumber int           NOT NULL PRIMARY KEY,
    Email     nvarchar(254) NOT NULL,
    FullName  nvarchar(120) NOT NULL
);
GO

-- The app's database principal joins this role. It can run api procedures and
-- nothing else. Procedures reach the dbo tables through ownership chaining, so the
-- explicit DENY documents intent without breaking them.
CREATE ROLE app_executor AUTHORIZATION dbo;
GO
GRANT EXECUTE ON SCHEMA::api TO app_executor;
DENY SELECT, INSERT, UPDATE, DELETE ON SCHEMA::dbo TO app_executor;
GO
