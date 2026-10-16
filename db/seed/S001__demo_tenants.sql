-- Demo data for local development and the end-to-end tests. Applied once, only when
-- the migrator runs with --seed. Tenant ids match the dev users in appsettings.Development.json.
-- Dates are relative to the time of seeding so the demo always has upcoming events.

SET NOCOUNT ON;

DECLARE @Contoso  uniqueidentifier = '6f1c8a52-3b7d-4e0a-9c11-2d5e8f4a7b01';
DECLARE @Fabrikam uniqueidentifier = '9a2e4c71-5d8b-4f3e-8a20-7b6c1d9e3f02';
DECLARE @Today    datetime2(0)     = DATEADD(hour, 9, CAST(CAST(SYSUTCDATETIME() AS date) AS datetime2(0)));
DECLARE @Now      datetime2(3)     = SYSUTCDATETIME();

------------------------------------------------------------------------------
-- Contoso Learning
------------------------------------------------------------------------------
EXEC admin.usp_Tenant_Provision @TenantId = @Contoso, @Name = N'Contoso Learning', @Slug = 'contoso', @InitialCredits = 1500;
EXEC admin.usp_Template_Upsert @TenantId = @Contoso, @TemplateId = '1b0e1c4a-0001-4000-8000-000000000001',
     @Title = N'AZ-104 Administrator Lab', @Cloud = 'azure', @DurationMinutes = 120, @CreditsPerSeat = 15;
EXEC admin.usp_Template_Upsert @TenantId = @Contoso, @TemplateId = '1b0e1c4a-0002-4000-8000-000000000002',
     @Title = N'AKS Platform Workshop', @Cloud = 'azure', @DurationMinutes = 90, @CreditsPerSeat = 20;
EXEC admin.usp_Template_Upsert @TenantId = @Contoso, @TemplateId = '1b0e1c4a-0003-4000-8000-000000000003',
     @Title = N'Serverless on AWS Lambda', @Cloud = 'aws', @DurationMinutes = 60, @CreditsPerSeat = 10;

EXEC sys.sp_set_session_context @key = N'TenantId', @value = @Contoso;

INSERT dbo.Events (EventId, TenantId, TemplateId, Title, StartsAt, EndsAt, Capacity, Status, CreatedBy)
VALUES
    ('2c9d0f00-0000-4000-8000-0000000000e1', @Contoso, '1b0e1c4a-0001-4000-8000-000000000001',
     N'AZ-104 Bootcamp, Cohort 12', DATEADD(day, 7, @Today), DATEADD(hour, 8, DATEADD(day, 7, @Today)), 12, 'open', N'seed'),
    ('2c9d0f00-0000-4000-8000-0000000000e2', @Contoso, '1b0e1c4a-0002-4000-8000-000000000002',
     N'Kubernetes on AKS Workshop', DATEADD(day, 3, @Today), DATEADD(hour, 6, DATEADD(day, 3, @Today)), 8, 'open', N'seed'),
    ('2c9d0f00-0000-4000-8000-0000000000e3', @Contoso, '1b0e1c4a-0003-4000-8000-000000000003',
     N'Serverless on AWS Lambda', DATEADD(day, 21, @Today), DATEADD(hour, 4, DATEADD(day, 21, @Today)), 20, 'draft', N'seed'),
    ('2c9d0f00-0000-4000-8000-0000000000e4', @Contoso, '1b0e1c4a-0001-4000-8000-000000000001',
     N'Azure Fundamentals Sprint', DATEADD(day, -10, @Today), DATEADD(hour, 8, DATEADD(day, -10, @Today)), 10, 'closed', N'seed');

INSERT dbo.Enrollments (TenantId, EventId, Email, FullName, Status, CreditsCharged, CreatedBy, CreatedAt, StatusChangedAt)
SELECT @Contoso, v.EventId, v.Email, v.FullName, v.Status,
       CASE WHEN v.Status = 'enrolled' THEN t.CreditsPerSeat ELSE 0 END,
       N'seed', DATEADD(minute, v.Seq, DATEADD(day, -5, @Now)), DATEADD(minute, v.Seq, DATEADD(day, -5, @Now))
FROM (VALUES
    ('2c9d0f00-0000-4000-8000-0000000000e1',  1, N'aarav.mehta@contoso.example',     N'Aarav Mehta',       'enrolled'),
    ('2c9d0f00-0000-4000-8000-0000000000e1',  2, N'diya.nair@contoso.example',       N'Diya Nair',         'enrolled'),
    ('2c9d0f00-0000-4000-8000-0000000000e1',  3, N'kabir.singh@contoso.example',     N'Kabir Singh',       'enrolled'),
    ('2c9d0f00-0000-4000-8000-0000000000e1',  4, N'emma.clarke@contoso.example',     N'Emma Clarke',       'enrolled'),
    ('2c9d0f00-0000-4000-8000-0000000000e1',  5, N'rohan.das@contoso.example',       N'Rohan Das',         'enrolled'),
    ('2c9d0f00-0000-4000-8000-0000000000e1',  6, N'isha.kapoor@contoso.example',     N'Isha Kapoor',       'enrolled'),
    ('2c9d0f00-0000-4000-8000-0000000000e1',  7, N'lucas.martin@contoso.example',    N'Lucas Martin',      'enrolled'),
    ('2c9d0f00-0000-4000-8000-0000000000e1',  8, N'sneha.iyer@contoso.example',      N'Sneha Iyer',        'enrolled'),
    ('2c9d0f00-0000-4000-8000-0000000000e1',  9, N'vikram.rao@contoso.example',      N'Vikram Rao',        'enrolled'),
    ('2c9d0f00-0000-4000-8000-0000000000e2', 10, N'ananya.ghosh@contoso.example',    N'Ananya Ghosh',      'enrolled'),
    ('2c9d0f00-0000-4000-8000-0000000000e2', 11, N'oliver.brown@contoso.example',    N'Oliver Brown',      'enrolled'),
    ('2c9d0f00-0000-4000-8000-0000000000e2', 12, N'priyanka.sen@contoso.example',    N'Priyanka Sen',      'enrolled'),
    ('2c9d0f00-0000-4000-8000-0000000000e2', 13, N'arjun.pillai@contoso.example',    N'Arjun Pillai',      'enrolled'),
    ('2c9d0f00-0000-4000-8000-0000000000e2', 14, N'maya.fernandes@contoso.example',  N'Maya Fernandes',    'enrolled'),
    ('2c9d0f00-0000-4000-8000-0000000000e2', 15, N'noah.wilson@contoso.example',     N'Noah Wilson',       'enrolled'),
    ('2c9d0f00-0000-4000-8000-0000000000e2', 16, N'tara.bose@contoso.example',       N'Tara Bose',         'enrolled'),
    ('2c9d0f00-0000-4000-8000-0000000000e2', 17, N'yusuf.khan@contoso.example',      N'Yusuf Khan',        'enrolled'),
    ('2c9d0f00-0000-4000-8000-0000000000e2', 18, N'nikhil.joshi@contoso.example',    N'Nikhil Joshi',      'waitlisted'),
    ('2c9d0f00-0000-4000-8000-0000000000e2', 19, N'grace.lee@contoso.example',       N'Grace Lee',         'waitlisted'),
    ('2c9d0f00-0000-4000-8000-0000000000e2', 20, N'farhan.ali@contoso.example',      N'Farhan Ali',        'waitlisted'),
    ('2c9d0f00-0000-4000-8000-0000000000e4', 21, N'aarav.mehta@contoso.example',     N'Aarav Mehta',       'enrolled'),
    ('2c9d0f00-0000-4000-8000-0000000000e4', 22, N'meghna.roy@contoso.example',      N'Meghna Roy',        'enrolled'),
    ('2c9d0f00-0000-4000-8000-0000000000e4', 23, N'samuel.green@contoso.example',    N'Samuel Green',      'enrolled'),
    ('2c9d0f00-0000-4000-8000-0000000000e4', 24, N'kavya.reddy@contoso.example',     N'Kavya Reddy',       'enrolled'),
    ('2c9d0f00-0000-4000-8000-0000000000e4', 25, N'aditya.verma@contoso.example',    N'Aditya Verma',      'enrolled'),
    ('2c9d0f00-0000-4000-8000-0000000000e4', 26, N'chloe.evans@contoso.example',     N'Chloe Evans',       'enrolled'),
    ('2c9d0f00-0000-4000-8000-0000000000e4', 27, N'ritika.shah@contoso.example',     N'Ritika Shah',       'enrolled'),
    ('2c9d0f00-0000-4000-8000-0000000000e4', 28, N'dev.malhotra@contoso.example',    N'Dev Malhotra',      'enrolled'),
    ('2c9d0f00-0000-4000-8000-0000000000e4', 29, N'zara.hussain@contoso.example',    N'Zara Hussain',      'enrolled'),
    ('2c9d0f00-0000-4000-8000-0000000000e4', 30, N'liam.walker@contoso.example',     N'Liam Walker',       'enrolled')
) AS v (EventId, Seq, Email, FullName, Status)
JOIN dbo.Events AS e ON e.EventId = v.EventId
JOIN dbo.LabTemplates AS t ON t.TemplateId = e.TemplateId;

INSERT dbo.CreditLedger (TenantId, Delta, Reason, EventId, EnrollmentId, CreatedAt)
SELECT TenantId, -CreditsCharged, 'enrollment', EventId, EnrollmentId, CreatedAt
FROM dbo.Enrollments
WHERE TenantId = @Contoso AND CreditsCharged > 0;

UPDATE dbo.TenantCredits
SET Balance = Balance - (SELECT SUM(CreditsCharged) FROM dbo.Enrollments WHERE TenantId = @Contoso)
WHERE TenantId = @Contoso;

------------------------------------------------------------------------------
-- Fabrikam Academy: a second tenant that must never see Contoso data
------------------------------------------------------------------------------
EXEC admin.usp_Tenant_Provision @TenantId = @Fabrikam, @Name = N'Fabrikam Academy', @Slug = 'fabrikam', @InitialCredits = 400;
EXEC admin.usp_Template_Upsert @TenantId = @Fabrikam, @TemplateId = '1b0e1c4a-0004-4000-8000-000000000004',
     @Title = N'GKE Fundamentals', @Cloud = 'gcp', @DurationMinutes = 90, @CreditsPerSeat = 12;

EXEC sys.sp_set_session_context @key = N'TenantId', @value = @Fabrikam;

INSERT dbo.Events (EventId, TenantId, TemplateId, Title, StartsAt, EndsAt, Capacity, Status, CreatedBy)
VALUES ('2c9d0f00-0000-4000-8000-0000000000f1', @Fabrikam, '1b0e1c4a-0004-4000-8000-000000000004',
        N'GKE Fundamentals, Spring Cohort', DATEADD(day, 5, @Today), DATEADD(hour, 6, DATEADD(day, 5, @Today)), 15, 'open', N'seed');

INSERT dbo.Enrollments (TenantId, EventId, Email, FullName, Status, CreditsCharged, CreatedBy, CreatedAt, StatusChangedAt)
SELECT @Fabrikam, '2c9d0f00-0000-4000-8000-0000000000f1', v.Email, v.FullName, 'enrolled', 12, N'seed',
       DATEADD(minute, v.Seq, DATEADD(day, -2, @Now)), DATEADD(minute, v.Seq, DATEADD(day, -2, @Now))
FROM (VALUES
    (1, N'hannah.schmidt@fabrikam.example', N'Hannah Schmidt'),
    (2, N'mateo.garcia@fabrikam.example',   N'Mateo Garcia'),
    (3, N'ines.costa@fabrikam.example',     N'Ines Costa')
) AS v (Seq, Email, FullName);

INSERT dbo.CreditLedger (TenantId, Delta, Reason, EventId, EnrollmentId, CreatedAt)
SELECT TenantId, -CreditsCharged, 'enrollment', EventId, EnrollmentId, CreatedAt
FROM dbo.Enrollments
WHERE TenantId = @Fabrikam AND CreditsCharged > 0;

UPDATE dbo.TenantCredits
SET Balance = Balance - (SELECT SUM(CreditsCharged) FROM dbo.Enrollments WHERE TenantId = @Fabrikam)
WHERE TenantId = @Fabrikam;

