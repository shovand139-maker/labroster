-- Event procedures. Tenant and user come from SESSION_CONTEXT, never from parameters.
-- Row-level security filters every table read here; the explicit TenantId
-- predicates below are defence in depth and help the optimiser pick the tenant index.

CREATE OR ALTER FUNCTION dbo.fn_LikeContains (@Search nvarchar(100))
RETURNS nvarchar(220)
WITH SCHEMABINDING
AS
BEGIN
    DECLARE @Trimmed nvarchar(100) = NULLIF(LTRIM(RTRIM(@Search)), N'');
    IF @Trimmed IS NULL
        RETURN NULL;
    -- Escape LIKE wildcards so user input is matched literally.
    RETURN N'%' + REPLACE(REPLACE(REPLACE(@Trimmed, N'[', N'[[]'), N'%', N'[%]'), N'_', N'[_]') + N'%';
END;
GO

CREATE OR ALTER PROCEDURE api.usp_Event_List
    @Search   nvarchar(100) = NULL,
    @Status   varchar(12)   = NULL,
    @Page     int           = 1,
    @PageSize int           = 20
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @TenantId uniqueidentifier = CAST(SESSION_CONTEXT(N'TenantId') AS uniqueidentifier);
    IF @TenantId IS NULL
        THROW 51001, N'internal|Session context has no TenantId.', 1;
    IF @Page < 1 OR @PageSize NOT BETWEEN 1 AND 100
        THROW 50400, N'invalid_paging|page must be at least 1 and pageSize between 1 and 100.', 1;
    IF @Status IS NOT NULL AND @Status NOT IN ('draft', 'open', 'closed', 'cancelled')
        THROW 50400, N'invalid_status|status must be draft, open, closed or cancelled.', 1;

    DECLARE @Pattern nvarchar(220) = dbo.fn_LikeContains(@Search);

    SELECT COUNT(*) AS TotalCount
    FROM dbo.Events AS e
    WHERE e.TenantId = @TenantId
      AND (@Status IS NULL OR e.Status = @Status)
      AND (@Pattern IS NULL OR e.Title LIKE @Pattern);

    SELECT v.EventId, v.Title, v.TemplateId, v.TemplateTitle, v.Cloud, v.CreditsPerSeat,
           v.StartsAt, v.EndsAt, v.Capacity, v.Status, v.Enrolled, v.Waitlisted,
           v.CreatedBy, v.CreatedAt, v.UpdatedAt, v.RowVer
    FROM dbo.EventView AS v
    WHERE v.TenantId = @TenantId
      AND (@Status IS NULL OR v.Status = @Status)
      AND (@Pattern IS NULL OR v.Title LIKE @Pattern)
    ORDER BY v.StartsAt, v.EventId
    OFFSET (@Page - 1) * @PageSize ROWS FETCH NEXT @PageSize ROWS ONLY;
END;
GO

CREATE OR ALTER PROCEDURE api.usp_Event_Get
    @EventId uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;

    IF NOT EXISTS (SELECT 1 FROM dbo.Events WHERE EventId = @EventId)
        THROW 50404, N'event_not_found|Event not found.', 1;

    SELECT v.EventId, v.Title, v.TemplateId, v.TemplateTitle, v.Cloud, v.CreditsPerSeat,
           v.StartsAt, v.EndsAt, v.Capacity, v.Status, v.Enrolled, v.Waitlisted,
           v.CreatedBy, v.CreatedAt, v.UpdatedAt, v.RowVer
    FROM dbo.EventView AS v
    WHERE v.EventId = @EventId;
END;
GO

CREATE OR ALTER PROCEDURE api.usp_Event_Create
    @TemplateId uniqueidentifier,
    @Title      nvarchar(160),
    @StartsAt   datetime2(0),
    @EndsAt     datetime2(0),
    @Capacity   int
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @TenantId uniqueidentifier = CAST(SESSION_CONTEXT(N'TenantId') AS uniqueidentifier);
    DECLARE @UserId   nvarchar(64)     = CAST(SESSION_CONTEXT(N'UserId') AS nvarchar(64));
    IF @TenantId IS NULL OR @UserId IS NULL
        THROW 51001, N'internal|Session context has no TenantId or UserId.', 1;

    SET @Title = LTRIM(RTRIM(@Title));
    IF LEN(@Title) < 3
        THROW 50400, N'invalid_title|title must be at least 3 characters.', 1;
    IF @EndsAt <= @StartsAt
        THROW 50400, N'invalid_window|The event must end after it starts.', 1;
    IF @Capacity NOT BETWEEN 1 AND 1000
        THROW 50400, N'invalid_capacity|capacity must be between 1 and 1000.', 1;
    IF NOT EXISTS (SELECT 1 FROM dbo.LabTemplates WHERE TemplateId = @TemplateId)
        THROW 50422, N'unknown_template|templateId does not match a lab template in your organization.', 1;

    DECLARE @Created TABLE (EventId uniqueidentifier NOT NULL);
    DECLARE @Details nvarchar(2000) =
        (SELECT @Title AS title, @Capacity AS capacity FOR JSON PATH, WITHOUT_ARRAY_WRAPPER);

    BEGIN TRANSACTION;

        INSERT dbo.Events (TenantId, TemplateId, Title, StartsAt, EndsAt, Capacity, CreatedBy)
        OUTPUT inserted.EventId INTO @Created (EventId)
        VALUES (@TenantId, @TemplateId, @Title, @StartsAt, @EndsAt, @Capacity, @UserId);

        DECLARE @EventId uniqueidentifier = (SELECT EventId FROM @Created);

        INSERT dbo.AuditLog (TenantId, ActorId, Action, EntityId, Details)
        VALUES (@TenantId, @UserId, 'event.created', @EventId, @Details);

    COMMIT TRANSACTION;

    EXEC api.usp_Event_Get @EventId = @EventId;
END;
GO

CREATE OR ALTER PROCEDURE api.usp_Event_Update
    @EventId        uniqueidentifier,
    @Title          nvarchar(160),
    @StartsAt       datetime2(0),
    @EndsAt         datetime2(0),
    @Capacity       int,
    @ExpectedRowVer binary(8)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @TenantId uniqueidentifier = CAST(SESSION_CONTEXT(N'TenantId') AS uniqueidentifier);
    DECLARE @UserId   nvarchar(64)     = CAST(SESSION_CONTEXT(N'UserId') AS nvarchar(64));
    IF @TenantId IS NULL OR @UserId IS NULL
        THROW 51001, N'internal|Session context has no TenantId or UserId.', 1;

    SET @Title = LTRIM(RTRIM(@Title));
    IF LEN(@Title) < 3
        THROW 50400, N'invalid_title|title must be at least 3 characters.', 1;
    IF @EndsAt <= @StartsAt
        THROW 50400, N'invalid_window|The event must end after it starts.', 1;
    IF @Capacity NOT BETWEEN 1 AND 1000
        THROW 50400, N'invalid_capacity|capacity must be between 1 and 1000.', 1;

    BEGIN TRANSACTION;

        DECLARE @CurrentStatus varchar(12), @CurrentRowVer binary(8), @OldCapacity int;
        SELECT @CurrentStatus = Status, @CurrentRowVer = RowVer, @OldCapacity = Capacity
        FROM dbo.Events WITH (UPDLOCK, HOLDLOCK)
        WHERE EventId = @EventId AND TenantId = @TenantId;

        IF @CurrentStatus IS NULL
            THROW 50404, N'event_not_found|Event not found.', 1;
        IF @ExpectedRowVer IS NULL OR @CurrentRowVer <> @ExpectedRowVer
            THROW 50412, N'stale_etag|The event changed since you loaded it. Reload it and try again.', 1;
        IF @CurrentStatus = 'cancelled'
            THROW 50409, N'event_cancelled|A cancelled event cannot be edited.', 1;

        DECLARE @Enrolled int =
            (SELECT COUNT(*) FROM dbo.Enrollments WHERE EventId = @EventId AND Status = 'enrolled');
        IF @Capacity < @Enrolled
        BEGIN
            DECLARE @CapacityMessage nvarchar(400) = CONCAT(
                N'capacity_below_enrolled|Capacity cannot be lower than the ', @Enrolled,
                N' learners already enrolled. Cancel enrollments first.');
            THROW 50409, @CapacityMessage, 1;
        END;

        UPDATE dbo.Events
        SET Title = @Title, StartsAt = @StartsAt, EndsAt = @EndsAt, Capacity = @Capacity,
            UpdatedAt = SYSUTCDATETIME()
        WHERE EventId = @EventId;

        DECLARE @Promoted int = 0;
        IF @Capacity > @OldCapacity AND @CurrentStatus = 'open'
            EXEC dbo.usp_PromoteWaitlist @EventId = @EventId, @PromotedCount = @Promoted OUTPUT;

        DECLARE @Details nvarchar(2000) =
            (SELECT @OldCapacity AS fromCapacity, @Capacity AS toCapacity, @Promoted AS promoted
             FOR JSON PATH, WITHOUT_ARRAY_WRAPPER);
        INSERT dbo.AuditLog (TenantId, ActorId, Action, EntityId, Details)
        VALUES (@TenantId, @UserId, 'event.updated', @EventId, @Details);

    COMMIT TRANSACTION;

    EXEC api.usp_Event_Get @EventId = @EventId;
END;
GO

-- Allowed transitions:
--   draft  -> open | cancelled
--   open   -> closed | cancelled
--   closed -> open | cancelled
-- Cancelling releases every seat and refunds the credits that were charged.
CREATE OR ALTER PROCEDURE api.usp_Event_SetStatus
    @EventId        uniqueidentifier,
    @Status         varchar(12),
    @ExpectedRowVer binary(8)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @TenantId uniqueidentifier = CAST(SESSION_CONTEXT(N'TenantId') AS uniqueidentifier);
    DECLARE @UserId   nvarchar(64)     = CAST(SESSION_CONTEXT(N'UserId') AS nvarchar(64));
    IF @TenantId IS NULL OR @UserId IS NULL
        THROW 51001, N'internal|Session context has no TenantId or UserId.', 1;
    IF @Status IS NULL OR @Status NOT IN ('open', 'closed', 'cancelled')
        THROW 50400, N'invalid_status|status must be open, closed or cancelled.', 1;

    BEGIN TRANSACTION;

        DECLARE @Current varchar(12), @CurrentRowVer binary(8);
        SELECT @Current = Status, @CurrentRowVer = RowVer
        FROM dbo.Events WITH (UPDLOCK, HOLDLOCK)
        WHERE EventId = @EventId AND TenantId = @TenantId;

        IF @Current IS NULL
            THROW 50404, N'event_not_found|Event not found.', 1;
        IF @ExpectedRowVer IS NULL OR @CurrentRowVer <> @ExpectedRowVer
            THROW 50412, N'stale_etag|The event changed since you loaded it. Reload it and try again.', 1;
        IF NOT (   (@Current = 'draft'  AND @Status IN ('open', 'cancelled'))
                OR (@Current = 'open'   AND @Status IN ('closed', 'cancelled'))
                OR (@Current = 'closed' AND @Status IN ('open', 'cancelled')))
        BEGIN
            DECLARE @TransitionMessage nvarchar(400) =
                CONCAT(N'invalid_transition|An event cannot move from ', @Current, N' to ', @Status, N'.');
            THROW 50409, @TransitionMessage, 1;
        END;

        UPDATE dbo.Events
        SET Status = @Status, UpdatedAt = SYSUTCDATETIME()
        WHERE EventId = @EventId;

        DECLARE @Refunded int = 0, @Released int = 0, @Promoted int = 0;

        IF @Status = 'cancelled'
        BEGIN
            DECLARE @Cancelled TABLE (EnrollmentId uniqueidentifier NOT NULL PRIMARY KEY, Credits int NOT NULL);

            UPDATE dbo.Enrollments
            SET Status = 'cancelled', CreditsCharged = 0, StatusChangedAt = SYSUTCDATETIME()
            OUTPUT inserted.EnrollmentId, deleted.CreditsCharged INTO @Cancelled (EnrollmentId, Credits)
            WHERE EventId = @EventId AND Status <> 'cancelled';

            SELECT @Released = COUNT(*), @Refunded = ISNULL(SUM(Credits), 0) FROM @Cancelled;

            IF @Refunded > 0
            BEGIN
                UPDATE dbo.TenantCredits
                SET Balance = Balance + @Refunded, UpdatedAt = SYSUTCDATETIME()
                WHERE TenantId = @TenantId;

                INSERT dbo.CreditLedger (TenantId, Delta, Reason, EventId, EnrollmentId)
                SELECT @TenantId, Credits, 'refund', @EventId, EnrollmentId
                FROM @Cancelled
                WHERE Credits > 0;
            END;
        END
        ELSE IF @Status = 'open'
        BEGIN
            EXEC dbo.usp_PromoteWaitlist @EventId = @EventId, @PromotedCount = @Promoted OUTPUT;
        END;

        DECLARE @Details nvarchar(2000) =
            (SELECT @Current AS [from], @Status AS [to], @Released AS released, @Refunded AS refunded, @Promoted AS promoted
             FOR JSON PATH, WITHOUT_ARRAY_WRAPPER);
        INSERT dbo.AuditLog (TenantId, ActorId, Action, EntityId, Details)
        VALUES (@TenantId, @UserId, 'event.status_changed', @EventId, @Details);

    COMMIT TRANSACTION;

    EXEC api.usp_Event_Get @EventId = @EventId;
END;
GO
