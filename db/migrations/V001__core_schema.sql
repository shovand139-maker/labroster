-- Core tables. Every tenant-owned table carries TenantId so row-level security
-- (V002) can filter it. Tables live in dbo and are never granted to the app:
-- the app only executes procedures in the api schema.

CREATE TABLE dbo.Tenants
(
    TenantId   uniqueidentifier NOT NULL CONSTRAINT PK_Tenants PRIMARY KEY,
    Name       nvarchar(120)    NOT NULL,
    Slug       varchar(40)      NOT NULL CONSTRAINT UQ_Tenants_Slug UNIQUE,
    CreatedAt  datetime2(3)     NOT NULL CONSTRAINT DF_Tenants_CreatedAt DEFAULT SYSUTCDATETIME()
);
GO

-- Cached balance, kept in step with CreditLedger inside the same transaction.
-- The CHECK constraint is the last line of defence against overspending.
CREATE TABLE dbo.TenantCredits
(
    TenantId   uniqueidentifier NOT NULL
        CONSTRAINT PK_TenantCredits PRIMARY KEY
        CONSTRAINT FK_TenantCredits_Tenants REFERENCES dbo.Tenants (TenantId),
    Balance    int              NOT NULL CONSTRAINT CK_TenantCredits_Balance CHECK (Balance >= 0),
    UpdatedAt  datetime2(3)     NOT NULL CONSTRAINT DF_TenantCredits_UpdatedAt DEFAULT SYSUTCDATETIME()
);
GO

CREATE TABLE dbo.LabTemplates
(
    TemplateId      uniqueidentifier NOT NULL CONSTRAINT PK_LabTemplates PRIMARY KEY,
    TenantId        uniqueidentifier NOT NULL CONSTRAINT FK_LabTemplates_Tenants REFERENCES dbo.Tenants (TenantId),
    Title           nvarchar(120)    NOT NULL,
    Cloud           varchar(10)      NOT NULL CONSTRAINT CK_LabTemplates_Cloud CHECK (Cloud IN ('azure', 'aws', 'gcp')),
    DurationMinutes int              NOT NULL CONSTRAINT CK_LabTemplates_Duration CHECK (DurationMinutes BETWEEN 15 AND 480),
    CreditsPerSeat  int              NOT NULL CONSTRAINT CK_LabTemplates_Credits CHECK (CreditsPerSeat BETWEEN 1 AND 500),
    CreatedAt       datetime2(3)     NOT NULL CONSTRAINT DF_LabTemplates_CreatedAt DEFAULT SYSUTCDATETIME()
);
GO

CREATE TABLE dbo.Events
(
    EventId    uniqueidentifier NOT NULL CONSTRAINT PK_Events PRIMARY KEY
                                CONSTRAINT DF_Events_EventId DEFAULT NEWSEQUENTIALID(),
    TenantId   uniqueidentifier NOT NULL CONSTRAINT FK_Events_Tenants REFERENCES dbo.Tenants (TenantId),
    TemplateId uniqueidentifier NOT NULL CONSTRAINT FK_Events_LabTemplates REFERENCES dbo.LabTemplates (TemplateId),
    Title      nvarchar(160)    NOT NULL,
    StartsAt   datetime2(0)     NOT NULL,
    EndsAt     datetime2(0)     NOT NULL,
    Capacity   int              NOT NULL CONSTRAINT CK_Events_Capacity CHECK (Capacity BETWEEN 1 AND 1000),
    Status     varchar(12)      NOT NULL CONSTRAINT DF_Events_Status DEFAULT 'draft'
                                CONSTRAINT CK_Events_Status CHECK (Status IN ('draft', 'open', 'closed', 'cancelled')),
    CreatedBy  nvarchar(64)     NOT NULL,
    CreatedAt  datetime2(3)     NOT NULL CONSTRAINT DF_Events_CreatedAt DEFAULT SYSUTCDATETIME(),
    UpdatedAt  datetime2(3)     NOT NULL CONSTRAINT DF_Events_UpdatedAt DEFAULT SYSUTCDATETIME(),
    RowVer     rowversion       NOT NULL,
    CONSTRAINT CK_Events_Window CHECK (EndsAt > StartsAt)
);
GO

CREATE INDEX IX_Events_Tenant_StartsAt
    ON dbo.Events (TenantId, StartsAt)
    INCLUDE (Title, Status, Capacity, TemplateId);
GO

CREATE TABLE dbo.Enrollments
(
    EnrollmentId    uniqueidentifier NOT NULL CONSTRAINT PK_Enrollments PRIMARY KEY
                                     CONSTRAINT DF_Enrollments_EnrollmentId DEFAULT NEWSEQUENTIALID(),
    TenantId        uniqueidentifier NOT NULL CONSTRAINT FK_Enrollments_Tenants REFERENCES dbo.Tenants (TenantId),
    EventId         uniqueidentifier NOT NULL CONSTRAINT FK_Enrollments_Events REFERENCES dbo.Events (EventId),
    Email           nvarchar(254)    NOT NULL,
    FullName        nvarchar(120)    NOT NULL,
    Status          varchar(12)      NOT NULL CONSTRAINT CK_Enrollments_Status CHECK (Status IN ('enrolled', 'waitlisted', 'cancelled')),
    CreditsCharged  int              NOT NULL CONSTRAINT DF_Enrollments_Credits DEFAULT 0
                                     CONSTRAINT CK_Enrollments_Credits CHECK (CreditsCharged >= 0),
    IdempotencyKey  varchar(80)      NULL,
    CreatedBy       nvarchar(64)     NOT NULL,
    CreatedAt       datetime2(3)     NOT NULL CONSTRAINT DF_Enrollments_CreatedAt DEFAULT SYSUTCDATETIME(),
    StatusChangedAt datetime2(3)     NOT NULL CONSTRAINT DF_Enrollments_StatusChangedAt DEFAULT SYSUTCDATETIME(),
    RowVer          rowversion       NOT NULL
);
GO

-- One live enrollment per person per event. Cancelled rows are history and may repeat.
CREATE UNIQUE INDEX UX_Enrollments_Event_Email_Active
    ON dbo.Enrollments (EventId, Email)
    WHERE Status <> 'cancelled';
GO

CREATE UNIQUE INDEX UX_Enrollments_Event_IdempotencyKey
    ON dbo.Enrollments (EventId, IdempotencyKey)
    WHERE IdempotencyKey IS NOT NULL;
GO

CREATE INDEX IX_Enrollments_Event_Status
    ON dbo.Enrollments (EventId, Status, StatusChangedAt)
    INCLUDE (Email, FullName, CreditsCharged);
GO

-- Append-only history of every credit movement. Balance = SUM(Delta).
CREATE TABLE dbo.CreditLedger
(
    LedgerId      bigint IDENTITY(1, 1) NOT NULL CONSTRAINT PK_CreditLedger PRIMARY KEY,
    TenantId      uniqueidentifier      NOT NULL CONSTRAINT FK_CreditLedger_Tenants REFERENCES dbo.Tenants (TenantId),
    Delta         int                   NOT NULL CONSTRAINT CK_CreditLedger_Delta CHECK (Delta <> 0),
    Reason        varchar(20)           NOT NULL CONSTRAINT CK_CreditLedger_Reason CHECK (Reason IN ('allocation', 'enrollment', 'refund')),
    EventId       uniqueidentifier      NULL,
    EnrollmentId  uniqueidentifier      NULL,
    CreatedAt     datetime2(3)          NOT NULL CONSTRAINT DF_CreditLedger_CreatedAt DEFAULT SYSUTCDATETIME()
);
GO

CREATE INDEX IX_CreditLedger_Tenant_CreatedAt ON dbo.CreditLedger (TenantId, CreatedAt) INCLUDE (Delta, Reason);
GO

CREATE TABLE dbo.AuditLog
(
    AuditId    bigint IDENTITY(1, 1) NOT NULL CONSTRAINT PK_AuditLog PRIMARY KEY,
    TenantId   uniqueidentifier      NOT NULL CONSTRAINT FK_AuditLog_Tenants REFERENCES dbo.Tenants (TenantId),
    ActorId    nvarchar(64)          NOT NULL,
    Action     varchar(40)           NOT NULL,
    EntityId   uniqueidentifier      NULL,
    Details    nvarchar(2000)        NULL CONSTRAINT CK_AuditLog_Details CHECK (Details IS NULL OR ISJSON(Details) = 1),
    CreatedAt  datetime2(3)          NOT NULL CONSTRAINT DF_AuditLog_CreatedAt DEFAULT SYSUTCDATETIME()
);
GO

CREATE INDEX IX_AuditLog_Tenant_CreatedAt ON dbo.AuditLog (TenantId, CreatedAt DESC);
GO
