-- Enrollment procedures.
--
-- Concurrency: every procedure that changes seats locks the event row first
-- (UPDLOCK, HOLDLOCK) and the tenant's credit row second. That fixed order
-- serialises seat allocation per event and keeps these procedures deadlock-free
-- with each other.

CREATE OR ALTER PROCEDURE api.usp_Enrollment_Create
    @EventId        uniqueidentifier,
    @Email          nvarchar(254),
    @FullName       nvarchar(120),
    @IdempotencyKey varchar(80) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @TenantId uniqueidentifier = CAST(SESSION_CONTEXT(N'TenantId') AS uniqueidentifier);
    DECLARE @UserId   nvarchar(64)     = CAST(SESSION_CONTEXT(N'UserId') AS nvarchar(64));
    IF @TenantId IS NULL OR @UserId IS NULL
        THROW 51001, N'internal|Session context has no TenantId or UserId.', 1;

    SET @Email = LOWER(LTRIM(RTRIM(@Email)));
    SET @FullName = LTRIM(RTRIM(@FullName));
    IF @Email IS NULL OR @Email NOT LIKE N'_%@_%._%' OR @Email LIKE N'% %'
        THROW 50400, N'invalid_email|email is not a valid address.', 1;
    IF @FullName IS NULL OR LEN(@FullName) < 2
        THROW 50400, N'invalid_name|fullName must be at least 2 characters.', 1;

    DECLARE @EnrollmentId uniqueidentifier, @Replayed bit = 0;

    BEGIN TRANSACTION;

        DECLARE @EventStatus varchar(12), @Capacity int, @Cost int;
        SELECT @EventStatus = e.Status, @Capacity = e.Capacity, @Cost = t.CreditsPerSeat
        FROM dbo.Events AS e WITH (UPDLOCK, HOLDLOCK)
        JOIN dbo.LabTemplates AS t ON t.TemplateId = e.TemplateId
        WHERE e.EventId = @EventId AND e.TenantId = @TenantId;

        IF @EventStatus IS NULL
            THROW 50404, N'event_not_found|Event not found.', 1;

        -- A retried request with the same key returns the original enrollment.
        IF @IdempotencyKey IS NOT NULL
            SELECT @EnrollmentId = EnrollmentId
            FROM dbo.Enrollments
            WHERE EventId = @EventId AND IdempotencyKey = @IdempotencyKey;

        IF @EnrollmentId IS NOT NULL
        BEGIN
            SET @Replayed = 1;
        END
        ELSE
        BEGIN
            IF @EventStatus <> 'open'
                THROW 50409, N'event_not_open|Learners can only be enrolled while the event is open.', 1;
            IF EXISTS (SELECT 1 FROM dbo.Enrollments
                       WHERE EventId = @EventId AND Email = @Email AND Status <> 'cancelled')
                THROW 50409, N'already_enrolled|This learner is already on the roster for this event.', 1;

            DECLARE @Enrolled int =
                (SELECT COUNT(*) FROM dbo.Enrollments WHERE EventId = @EventId AND Status = 'enrolled');
            DECLARE @NewStatus varchar(12) = CASE WHEN @Enrolled < @Capacity THEN 'enrolled' ELSE 'waitlisted' END;
            DECLARE @Charged int = 0;

            IF @NewStatus = 'enrolled'
            BEGIN
                -- Atomic conditional debit: succeeds only if the balance covers the seat.
                UPDATE dbo.TenantCredits
                SET Balance = Balance - @Cost, UpdatedAt = SYSUTCDATETIME()
                WHERE TenantId = @TenantId AND Balance >= @Cost;

                IF @@ROWCOUNT = 0
                    THROW 50402, N'insufficient_credits|Your organization does not have enough credits for another seat.', 1;
                SET @Charged = @Cost;
            END;

            DECLARE @Inserted TABLE (EnrollmentId uniqueidentifier NOT NULL);
            INSERT dbo.Enrollments (TenantId, EventId, Email, FullName, Status, CreditsCharged, IdempotencyKey, CreatedBy)
            OUTPUT inserted.EnrollmentId INTO @Inserted (EnrollmentId)
            VALUES (@TenantId, @EventId, @Email, @FullName, @NewStatus, @Charged, @IdempotencyKey, @UserId);

            SET @EnrollmentId = (SELECT EnrollmentId FROM @Inserted);

            IF @Charged > 0
                INSERT dbo.CreditLedger (TenantId, Delta, Reason, EventId, EnrollmentId)
                VALUES (@TenantId, -@Charged, 'enrollment', @EventId, @EnrollmentId);

            DECLARE @Details nvarchar(2000) =
                (SELECT @EventId AS eventId, @NewStatus AS status, @Charged AS credits
                 FOR JSON PATH, WITHOUT_ARRAY_WRAPPER);
            INSERT dbo.AuditLog (TenantId, ActorId, Action, EntityId, Details)
            VALUES (@TenantId, @UserId, 'enrollment.created', @EnrollmentId, @Details);
        END;

    COMMIT TRANSACTION;

    SELECT v.EnrollmentId, v.EventId, v.Email, v.FullName, v.Status, v.CreditsCharged,
           v.WaitlistPosition, v.CreatedAt, v.StatusChangedAt, @Replayed AS Replayed
    FROM dbo.EnrollmentView AS v
    WHERE v.EventId = @EventId AND v.EnrollmentId = @EnrollmentId;
END;
GO

-- Set-based bulk enrollment from a table-valued parameter.
-- Outcomes per input row: enrolled, waitlisted, already_enrolled, duplicate_in_file,
-- invalid_email, invalid_name, insufficient_credits.
CREATE OR ALTER PROCEDURE api.usp_Enrollment_BulkCreate
    @EventId  uniqueidentifier,
    @Learners api.LearnerList READONLY
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @TenantId uniqueidentifier = CAST(SESSION_CONTEXT(N'TenantId') AS uniqueidentifier);
    DECLARE @UserId   nvarchar(64)     = CAST(SESSION_CONTEXT(N'UserId') AS nvarchar(64));
    IF @TenantId IS NULL OR @UserId IS NULL
        THROW 51001, N'internal|Session context has no TenantId or UserId.', 1;

    IF NOT EXISTS (SELECT 1 FROM @Learners)
        THROW 50400, N'empty_roster|The roster contains no learners.', 1;
    IF (SELECT COUNT(*) FROM @Learners) > 500
        THROW 50400, N'roster_too_large|A roster upload is limited to 500 learners.', 1;

    DECLARE @Rows TABLE
    (
        RowNumber    int              NOT NULL PRIMARY KEY,
        Email        nvarchar(254)    NOT NULL,
        FullName     nvarchar(120)    NOT NULL,
        Outcome      varchar(24)      NOT NULL,
        EnrollmentId uniqueidentifier NULL
    );

    INSERT @Rows (RowNumber, Email, FullName, Outcome)
    SELECT RowNumber, LOWER(LTRIM(RTRIM(Email))), LTRIM(RTRIM(FullName)), 'pending'
    FROM @Learners;

    UPDATE @Rows SET Outcome = 'invalid_email'
    WHERE Email NOT LIKE N'_%@_%._%' OR Email LIKE N'% %';

    UPDATE @Rows SET Outcome = 'invalid_name'
    WHERE Outcome = 'pending' AND LEN(FullName) < 2;

    WITH Ranked AS
    (
        SELECT Outcome, ROW_NUMBER() OVER (PARTITION BY Email ORDER BY RowNumber) AS Seq
        FROM @Rows
        WHERE Outcome = 'pending'
    )
    UPDATE Ranked SET Outcome = 'duplicate_in_file' WHERE Seq > 1;

    BEGIN TRANSACTION;

        DECLARE @EventStatus varchar(12), @Capacity int, @Cost int;
        SELECT @EventStatus = e.Status, @Capacity = e.Capacity, @Cost = t.CreditsPerSeat
        FROM dbo.Events AS e WITH (UPDLOCK, HOLDLOCK)
        JOIN dbo.LabTemplates AS t ON t.TemplateId = e.TemplateId
        WHERE e.EventId = @EventId AND e.TenantId = @TenantId;

        IF @EventStatus IS NULL
            THROW 50404, N'event_not_found|Event not found.', 1;
        IF @EventStatus <> 'open'
            THROW 50409, N'event_not_open|Learners can only be enrolled while the event is open.', 1;

        UPDATE r SET Outcome = 'already_enrolled'
        FROM @Rows AS r
        WHERE r.Outcome = 'pending'
          AND EXISTS (SELECT 1 FROM dbo.Enrollments AS en
                      WHERE en.EventId = @EventId AND en.Email = r.Email AND en.Status <> 'cancelled');

        DECLARE @FreeSeats int = @Capacity -
            (SELECT COUNT(*) FROM dbo.Enrollments WHERE EventId = @EventId AND Status = 'enrolled');
        IF @FreeSeats < 0 SET @FreeSeats = 0;

        DECLARE @Balance int =
            (SELECT Balance FROM dbo.TenantCredits WITH (UPDLOCK, HOLDLOCK) WHERE TenantId = @TenantId);
        DECLARE @Affordable int = ISNULL(@Balance, 0) / @Cost;
        DECLARE @ToEnroll int = CASE WHEN @Affordable < @FreeSeats THEN @Affordable ELSE @FreeSeats END;

        -- Fill free seats in file order while credits last. Rows that would have had a
        -- seat but cannot be paid for are reported; rows beyond capacity join the waitlist.
        WITH Pending AS
        (
            SELECT Outcome, ROW_NUMBER() OVER (ORDER BY RowNumber) AS Seq
            FROM @Rows
            WHERE Outcome = 'pending'
        )
        UPDATE Pending
        SET Outcome = CASE WHEN Seq <= @ToEnroll THEN 'enrolled'
                           WHEN Seq <= @FreeSeats THEN 'insufficient_credits'
                           ELSE 'waitlisted' END;

        DECLARE @Inserted TABLE (EnrollmentId uniqueidentifier NOT NULL, Email nvarchar(254) NOT NULL);

        INSERT dbo.Enrollments (TenantId, EventId, Email, FullName, Status, CreditsCharged, CreatedBy)
        OUTPUT inserted.EnrollmentId, inserted.Email INTO @Inserted (EnrollmentId, Email)
        SELECT @TenantId, @EventId, Email, FullName, Outcome,
               CASE WHEN Outcome = 'enrolled' THEN @Cost ELSE 0 END, @UserId
        FROM @Rows
        WHERE Outcome IN ('enrolled', 'waitlisted')
        ORDER BY RowNumber;

        UPDATE r SET EnrollmentId = i.EnrollmentId
        FROM @Rows AS r
        JOIN @Inserted AS i ON i.Email = r.Email
        WHERE r.Outcome IN ('enrolled', 'waitlisted');

        DECLARE @NewlyEnrolled int = (SELECT COUNT(*) FROM @Rows WHERE Outcome = 'enrolled');
        DECLARE @Waitlisted int = (SELECT COUNT(*) FROM @Rows WHERE Outcome = 'waitlisted');

        IF @NewlyEnrolled > 0
        BEGIN
            UPDATE dbo.TenantCredits
            SET Balance = Balance - @NewlyEnrolled * @Cost, UpdatedAt = SYSUTCDATETIME()
            WHERE TenantId = @TenantId;

            INSERT dbo.CreditLedger (TenantId, Delta, Reason, EventId, EnrollmentId)
            SELECT @TenantId, -@Cost, 'enrollment', @EventId, EnrollmentId
            FROM @Rows
            WHERE Outcome = 'enrolled';
        END;

        DECLARE @Details nvarchar(2000) =
            (SELECT (SELECT COUNT(*) FROM @Rows) AS [rows], @NewlyEnrolled AS enrolled,
                    @Waitlisted AS waitlisted, @NewlyEnrolled * @Cost AS credits
             FOR JSON PATH, WITHOUT_ARRAY_WRAPPER);
        INSERT dbo.AuditLog (TenantId, ActorId, Action, EntityId, Details)
        VALUES (@TenantId, @UserId, 'enrollment.bulk_created', @EventId, @Details);

    COMMIT TRANSACTION;

    SELECT RowNumber, Email, Outcome, EnrollmentId
    FROM @Rows
    ORDER BY RowNumber;
END;
GO

-- Cancels an enrollment, refunds its credits and fills the freed seat from the waitlist.
CREATE OR ALTER PROCEDURE api.usp_Enrollment_Cancel
    @EnrollmentId uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @TenantId uniqueidentifier = CAST(SESSION_CONTEXT(N'TenantId') AS uniqueidentifier);
    DECLARE @UserId   nvarchar(64)     = CAST(SESSION_CONTEXT(N'UserId') AS nvarchar(64));
    IF @TenantId IS NULL OR @UserId IS NULL
        THROW 51001, N'internal|Session context has no TenantId or UserId.', 1;

    DECLARE @EventId uniqueidentifier =
        (SELECT EventId FROM dbo.Enrollments WHERE EnrollmentId = @EnrollmentId AND TenantId = @TenantId);
    IF @EventId IS NULL
        THROW 50404, N'enrollment_not_found|Enrollment not found.', 1;

    DECLARE @Promoted int = 0;

    BEGIN TRANSACTION;

        -- Lock order: event first, then the enrollment and credits.
        DECLARE @EventStatus varchar(12) =
            (SELECT Status FROM dbo.Events WITH (UPDLOCK, HOLDLOCK) WHERE EventId = @EventId);

        DECLARE @Status varchar(12), @Charged int;
        SELECT @Status = Status, @Charged = CreditsCharged
        FROM dbo.Enrollments WITH (UPDLOCK)
        WHERE EnrollmentId = @EnrollmentId;

        IF @Status = 'cancelled'
            THROW 50409, N'already_cancelled|This enrollment is already cancelled.', 1;

        UPDATE dbo.Enrollments
        SET Status = 'cancelled', CreditsCharged = 0, StatusChangedAt = SYSUTCDATETIME()
        WHERE EnrollmentId = @EnrollmentId;

        IF @Charged > 0
        BEGIN
            UPDATE dbo.TenantCredits
            SET Balance = Balance + @Charged, UpdatedAt = SYSUTCDATETIME()
            WHERE TenantId = @TenantId;

            INSERT dbo.CreditLedger (TenantId, Delta, Reason, EventId, EnrollmentId)
            VALUES (@TenantId, @Charged, 'refund', @EventId, @EnrollmentId);
        END;

        IF @Status = 'enrolled' AND @EventStatus = 'open'
            EXEC dbo.usp_PromoteWaitlist @EventId = @EventId, @PromotedCount = @Promoted OUTPUT;

        DECLARE @Details nvarchar(2000) =
            (SELECT @EventId AS eventId, @Status AS previousStatus, @Charged AS refunded, @Promoted AS promoted
             FOR JSON PATH, WITHOUT_ARRAY_WRAPPER);
        INSERT dbo.AuditLog (TenantId, ActorId, Action, EntityId, Details)
        VALUES (@TenantId, @UserId, 'enrollment.cancelled', @EnrollmentId, @Details);

    COMMIT TRANSACTION;

    SELECT @EnrollmentId AS EnrollmentId, @EventId AS EventId, @Status AS PreviousStatus,
           @Charged AS CreditsRefunded, @Promoted AS PromotedCount;
END;
GO

CREATE OR ALTER PROCEDURE api.usp_Roster_Get
    @EventId  uniqueidentifier,
    @Search   nvarchar(100) = NULL,
    @Status   varchar(12)   = NULL,
    @Page     int           = 1,
    @PageSize int           = 50
AS
BEGIN
    SET NOCOUNT ON;

    IF NOT EXISTS (SELECT 1 FROM dbo.Events WHERE EventId = @EventId)
        THROW 50404, N'event_not_found|Event not found.', 1;
    IF @Page < 1 OR @PageSize NOT BETWEEN 1 AND 200
        THROW 50400, N'invalid_paging|page must be at least 1 and pageSize between 1 and 200.', 1;
    IF @Status IS NOT NULL AND @Status NOT IN ('enrolled', 'waitlisted', 'cancelled')
        THROW 50400, N'invalid_status|status must be enrolled, waitlisted or cancelled.', 1;

    DECLARE @Pattern nvarchar(220) = dbo.fn_LikeContains(@Search);

    -- Search filters after the view numbers the waitlist, so positions stay true.
    SELECT COUNT(*) AS TotalCount
    FROM dbo.EnrollmentView AS v
    WHERE v.EventId = @EventId
      AND (@Status IS NULL OR v.Status = @Status)
      AND (@Pattern IS NULL OR v.Email LIKE @Pattern OR v.FullName LIKE @Pattern);

    SELECT v.EnrollmentId, v.EventId, v.Email, v.FullName, v.Status, v.CreditsCharged,
           v.WaitlistPosition, v.CreatedAt, v.StatusChangedAt, CAST(0 AS bit) AS Replayed
    FROM dbo.EnrollmentView AS v
    WHERE v.EventId = @EventId
      AND (@Status IS NULL OR v.Status = @Status)
      AND (@Pattern IS NULL OR v.Email LIKE @Pattern OR v.FullName LIKE @Pattern)
    ORDER BY CASE v.Status WHEN 'enrolled' THEN 0 WHEN 'waitlisted' THEN 1 ELSE 2 END,
             v.WaitlistPosition, v.FullName, v.EnrollmentId
    OFFSET (@Page - 1) * @PageSize ROWS FETCH NEXT @PageSize ROWS ONLY;
END;
GO
