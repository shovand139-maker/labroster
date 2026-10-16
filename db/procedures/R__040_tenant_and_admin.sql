-- Tenant-facing reads for the API, and admin procedures for operators.
-- admin.* procedures are not granted to the app role. They switch SESSION_CONTEXT to
-- the tenant they provision, so row-level security still checks every row they write.

CREATE OR ALTER PROCEDURE api.usp_Tenant_Get
AS
BEGIN
    SET NOCOUNT ON;

    IF NOT EXISTS (SELECT 1 FROM dbo.Tenants)
        THROW 50403, N'tenant_not_onboarded|Your organization has not been set up in LabRoster yet.', 1;

    SELECT t.TenantId, t.Name, t.Slug, ISNULL(c.Balance, 0) AS CreditBalance
    FROM dbo.Tenants AS t
    LEFT JOIN dbo.TenantCredits AS c ON c.TenantId = t.TenantId;
END;
GO

CREATE OR ALTER PROCEDURE api.usp_Template_List
AS
BEGIN
    SET NOCOUNT ON;

    SELECT TemplateId, Title, Cloud, DurationMinutes, CreditsPerSeat
    FROM dbo.LabTemplates
    ORDER BY Title;
END;
GO

CREATE OR ALTER PROCEDURE api.usp_Credits_Get
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @TenantId uniqueidentifier = CAST(SESSION_CONTEXT(N'TenantId') AS uniqueidentifier);
    IF @TenantId IS NULL
        THROW 51001, N'internal|Session context has no TenantId.', 1;

    DECLARE @Since datetime2(3) = DATEADD(day, -30, SYSUTCDATETIME());
    DECLARE @Balance int = ISNULL((SELECT Balance FROM dbo.TenantCredits WHERE TenantId = @TenantId), 0);

    SELECT
        @Balance                                                                                    AS Balance,
        ISNULL(SUM(CASE WHEN Reason = 'allocation' THEN Delta END), 0)                             AS Allocated,
        ISNULL(-SUM(CASE WHEN Reason = 'enrollment' AND CreatedAt >= @Since THEN Delta END), 0)     AS Spent30Days,
        ISNULL(SUM(CASE WHEN Reason = 'refund' AND CreatedAt >= @Since THEN Delta END), 0)          AS Refunded30Days
    FROM dbo.CreditLedger
    WHERE TenantId = @TenantId;

    SELECT TOP (25) l.LedgerId, l.Delta, l.Reason, l.EventId, e.Title AS EventTitle, l.CreatedAt
    FROM dbo.CreditLedger AS l
    LEFT JOIN dbo.Events AS e ON e.EventId = l.EventId
    WHERE l.TenantId = @TenantId
    ORDER BY l.CreatedAt DESC, l.LedgerId DESC;
END;
GO

CREATE OR ALTER PROCEDURE api.usp_Audit_List
    @Page     int = 1,
    @PageSize int = 50
AS
BEGIN
    SET NOCOUNT ON;

    IF @Page < 1 OR @PageSize NOT BETWEEN 1 AND 200
        THROW 50400, N'invalid_paging|page must be at least 1 and pageSize between 1 and 200.', 1;

    SELECT COUNT(*) AS TotalCount FROM dbo.AuditLog;

    SELECT AuditId, ActorId, Action, EntityId, Details, CreatedAt
    FROM dbo.AuditLog
    ORDER BY CreatedAt DESC, AuditId DESC
    OFFSET (@Page - 1) * @PageSize ROWS FETCH NEXT @PageSize ROWS ONLY;
END;
GO

CREATE OR ALTER PROCEDURE admin.usp_Tenant_Provision
    @TenantId       uniqueidentifier,
    @Name           nvarchar(120),
    @Slug           varchar(40),
    @InitialCredits int = 0
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @InitialCredits < 0
        THROW 50400, N'invalid_credits|InitialCredits cannot be negative.', 1;

    EXEC sys.sp_set_session_context @key = N'TenantId', @value = @TenantId;
    EXEC sys.sp_set_session_context @key = N'UserId', @value = N'provisioning';

    BEGIN TRANSACTION;

        IF NOT EXISTS (SELECT 1 FROM dbo.Tenants WHERE TenantId = @TenantId)
        BEGIN
            INSERT dbo.Tenants (TenantId, Name, Slug) VALUES (@TenantId, @Name, @Slug);
            INSERT dbo.TenantCredits (TenantId, Balance) VALUES (@TenantId, @InitialCredits);

            IF @InitialCredits > 0
                INSERT dbo.CreditLedger (TenantId, Delta, Reason) VALUES (@TenantId, @InitialCredits, 'allocation');

            DECLARE @Details nvarchar(2000) =
                (SELECT @Name AS name, @InitialCredits AS credits FOR JSON PATH, WITHOUT_ARRAY_WRAPPER);
            INSERT dbo.AuditLog (TenantId, ActorId, Action, EntityId, Details)
            VALUES (@TenantId, N'provisioning', 'tenant.provisioned', @TenantId, @Details);
        END;

    COMMIT TRANSACTION;
END;
GO

CREATE OR ALTER PROCEDURE admin.usp_Credits_Allocate
    @TenantId uniqueidentifier,
    @Credits  int
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    IF @Credits <= 0
        THROW 50400, N'invalid_credits|Credits must be positive.', 1;

    EXEC sys.sp_set_session_context @key = N'TenantId', @value = @TenantId;

    BEGIN TRANSACTION;
        UPDATE dbo.TenantCredits
        SET Balance = Balance + @Credits, UpdatedAt = SYSUTCDATETIME()
        WHERE TenantId = @TenantId;

        IF @@ROWCOUNT = 0
            THROW 50404, N'tenant_not_found|Tenant not found.', 1;

        INSERT dbo.CreditLedger (TenantId, Delta, Reason) VALUES (@TenantId, @Credits, 'allocation');
    COMMIT TRANSACTION;
END;
GO

CREATE OR ALTER PROCEDURE admin.usp_Template_Upsert
    @TenantId        uniqueidentifier,
    @TemplateId      uniqueidentifier,
    @Title           nvarchar(120),
    @Cloud           varchar(10),
    @DurationMinutes int,
    @CreditsPerSeat  int
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    EXEC sys.sp_set_session_context @key = N'TenantId', @value = @TenantId;

    BEGIN TRANSACTION;
        UPDATE dbo.LabTemplates
        SET Title = @Title, Cloud = @Cloud, DurationMinutes = @DurationMinutes, CreditsPerSeat = @CreditsPerSeat
        WHERE TemplateId = @TemplateId;

        IF @@ROWCOUNT = 0
            INSERT dbo.LabTemplates (TemplateId, TenantId, Title, Cloud, DurationMinutes, CreditsPerSeat)
            VALUES (@TemplateId, @TenantId, @Title, @Cloud, @DurationMinutes, @CreditsPerSeat);
    COMMIT TRANSACTION;
END;
GO
