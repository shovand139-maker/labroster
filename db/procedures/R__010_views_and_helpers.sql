-- Repeatable script: re-applied by the migrator whenever its checksum changes.
--
-- Error convention for every procedure:
--   THROW 50xxx, N'<code>|<message>', 1
--   50400-50499 become HTTP 400-499 in the API with <code> as the problem "code".
--   Anything else becomes a 500.
-- Procedures validate and THROW before returning any result set.

CREATE OR ALTER VIEW dbo.EnrollmentView
AS
SELECT
    en.EnrollmentId,
    en.TenantId,
    en.EventId,
    en.Email,
    en.FullName,
    en.Status,
    en.CreditsCharged,
    en.CreatedAt,
    en.StatusChangedAt,
    CASE WHEN en.Status = 'waitlisted'
         THEN ROW_NUMBER() OVER (PARTITION BY en.EventId, en.Status ORDER BY en.CreatedAt, en.EnrollmentId)
    END AS WaitlistPosition
FROM dbo.Enrollments AS en;
GO

CREATE OR ALTER VIEW dbo.EventView
AS
SELECT
    e.EventId,
    e.TenantId,
    e.Title,
    e.TemplateId,
    t.Title AS TemplateTitle,
    t.Cloud,
    t.CreditsPerSeat,
    e.StartsAt,
    e.EndsAt,
    e.Capacity,
    e.Status,
    ISNULL(c.Enrolled, 0)   AS Enrolled,
    ISNULL(c.Waitlisted, 0) AS Waitlisted,
    e.CreatedBy,
    e.CreatedAt,
    e.UpdatedAt,
    e.RowVer
FROM dbo.Events AS e
JOIN dbo.LabTemplates AS t ON t.TemplateId = e.TemplateId
OUTER APPLY
(
    SELECT
        SUM(CASE WHEN en.Status = 'enrolled' THEN 1 ELSE 0 END)   AS Enrolled,
        SUM(CASE WHEN en.Status = 'waitlisted' THEN 1 ELSE 0 END) AS Waitlisted
    FROM dbo.Enrollments AS en
    WHERE en.EventId = e.EventId
) AS c;
GO

-- Promotes waitlisted learners into free seats, oldest first, as far as credits allow.
-- Callers must already hold the event row lock inside an open transaction.
CREATE OR ALTER PROCEDURE dbo.usp_PromoteWaitlist
    @EventId       uniqueidentifier,
    @PromotedCount int OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @TenantId uniqueidentifier = CAST(SESSION_CONTEXT(N'TenantId') AS uniqueidentifier);
    DECLARE @UserId   nvarchar(64)     = CAST(SESSION_CONTEXT(N'UserId') AS nvarchar(64));
    SET @PromotedCount = 0;

    IF @@TRANCOUNT = 0
        THROW 51000, N'internal|dbo.usp_PromoteWaitlist must run inside a transaction.', 1;

    DECLARE @Capacity int, @Cost int;
    SELECT @Capacity = e.Capacity, @Cost = t.CreditsPerSeat
    FROM dbo.Events AS e
    JOIN dbo.LabTemplates AS t ON t.TemplateId = e.TemplateId
    WHERE e.EventId = @EventId AND e.Status = 'open';

    IF @Capacity IS NULL
        RETURN;

    DECLARE @FreeSeats int = @Capacity -
        (SELECT COUNT(*) FROM dbo.Enrollments WHERE EventId = @EventId AND Status = 'enrolled');

    DECLARE @Balance int =
        (SELECT Balance FROM dbo.TenantCredits WITH (UPDLOCK, HOLDLOCK) WHERE TenantId = @TenantId);

    DECLARE @ToPromote int =
        CASE WHEN @FreeSeats <= 0 THEN 0
             WHEN @Balance / @Cost < @FreeSeats THEN @Balance / @Cost
             ELSE @FreeSeats END;

    IF @ToPromote <= 0
        RETURN;

    DECLARE @Promoted TABLE (EnrollmentId uniqueidentifier NOT NULL PRIMARY KEY);

    WITH NextInLine AS
    (
        SELECT TOP (@ToPromote) EnrollmentId, Status, CreditsCharged, StatusChangedAt
        FROM dbo.Enrollments WITH (UPDLOCK)
        WHERE EventId = @EventId AND Status = 'waitlisted'
        ORDER BY CreatedAt, EnrollmentId
    )
    UPDATE NextInLine
    SET Status = 'enrolled', CreditsCharged = @Cost, StatusChangedAt = SYSUTCDATETIME()
    OUTPUT inserted.EnrollmentId INTO @Promoted (EnrollmentId);

    SET @PromotedCount = (SELECT COUNT(*) FROM @Promoted);
    IF @PromotedCount = 0
        RETURN;

    UPDATE dbo.TenantCredits
    SET Balance = Balance - @PromotedCount * @Cost, UpdatedAt = SYSUTCDATETIME()
    WHERE TenantId = @TenantId;

    INSERT dbo.CreditLedger (TenantId, Delta, Reason, EventId, EnrollmentId)
    SELECT @TenantId, -@Cost, 'enrollment', @EventId, EnrollmentId
    FROM @Promoted;

    INSERT dbo.AuditLog (TenantId, ActorId, Action, EntityId, Details)
    SELECT @TenantId, @UserId, 'enrollment.promoted', EnrollmentId,
           (SELECT @EventId AS eventId, @Cost AS credits FOR JSON PATH, WITHOUT_ARRAY_WRAPPER)
    FROM @Promoted;
END;
GO
