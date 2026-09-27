/*
 DynReport metadata schema v1
 Target: dedicated DynReport SQL database.
 Safe to re-run. Does not create the database itself.

 IMPORTANT:
 - Run with a deployment principal that may create schema/tables.
 - Runtime account should receive only the specific DML permissions it needs.
 - No data-source credentials are stored in these tables.
*/
SET XACT_ABORT ON;
GO

IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'dyn')
    EXEC(N'CREATE SCHEMA dyn AUTHORIZATION dbo');
GO

IF OBJECT_ID(N'dyn.SchemaVersion', N'U') IS NULL
BEGIN
    CREATE TABLE dyn.SchemaVersion
    (
        VersionNumber int NOT NULL CONSTRAINT PK_dyn_SchemaVersion PRIMARY KEY,
        AppliedUtc datetime2(3) NOT NULL CONSTRAINT DF_dyn_SchemaVersion_AppliedUtc DEFAULT SYSUTCDATETIME(),
        Description nvarchar(300) NOT NULL
    );
END;
GO

IF OBJECT_ID(N'dyn.Report', N'U') IS NULL
BEGIN
    CREATE TABLE dyn.Report
    (
        ReportId nvarchar(200) NOT NULL CONSTRAINT PK_dyn_Report PRIMARY KEY,
        [Path] nvarchar(600) NOT NULL,
        FolderPath nvarchar(600) NOT NULL,
        Title nvarchar(300) NOT NULL,
        [Description] nvarchar(2000) NULL,
        ActiveRevisionId uniqueidentifier NULL,
        IsDeleted bit NOT NULL CONSTRAINT DF_dyn_Report_IsDeleted DEFAULT (0),
        CreatedUtc datetime2(3) NOT NULL CONSTRAINT DF_dyn_Report_CreatedUtc DEFAULT SYSUTCDATETIME(),
        ModifiedUtc datetime2(3) NOT NULL CONSTRAINT DF_dyn_Report_ModifiedUtc DEFAULT SYSUTCDATETIME(),
        RowVersion rowversion NOT NULL
    );

    CREATE UNIQUE INDEX UX_dyn_Report_Path
        ON dyn.Report([Path])
        WHERE IsDeleted = 0;
END;
GO

IF OBJECT_ID(N'dyn.ReportRevision', N'U') IS NULL
BEGIN
    CREATE TABLE dyn.ReportRevision
    (
        RevisionId uniqueidentifier NOT NULL CONSTRAINT PK_dyn_ReportRevision PRIMARY KEY,
        ReportId nvarchar(200) NOT NULL,
        PackageVersion nvarchar(64) NOT NULL,
        FormatVersion nvarchar(32) NOT NULL,
        ContentHash char(64) NOT NULL,
        PackageContent varbinary(max) NULL,
        PackageLength bigint NOT NULL,
        CreatedUtc datetime2(3) NOT NULL CONSTRAINT DF_dyn_ReportRevision_CreatedUtc DEFAULT SYSUTCDATETIME(),
        CreatedBy nvarchar(300) NULL,
        IsValidated bit NOT NULL,
        ValidationSummary nvarchar(2000) NULL,

        CONSTRAINT FK_dyn_ReportRevision_Report
            FOREIGN KEY (ReportId) REFERENCES dyn.Report(ReportId)
    );

    CREATE UNIQUE INDEX UX_dyn_ReportRevision_Report_Hash
        ON dyn.ReportRevision(ReportId, ContentHash);

    CREATE INDEX IX_dyn_ReportRevision_Report_CreatedUtc
        ON dyn.ReportRevision(ReportId, CreatedUtc DESC);
END;
GO

IF NOT EXISTS
(
    SELECT 1
    FROM sys.foreign_keys
    WHERE name = N'FK_dyn_Report_ActiveRevision'
)
BEGIN
    ALTER TABLE dyn.Report
    ADD CONSTRAINT FK_dyn_Report_ActiveRevision
        FOREIGN KEY (ActiveRevisionId)
        REFERENCES dyn.ReportRevision(RevisionId);
END;
GO

IF OBJECT_ID(N'dyn.DataSourceProfile', N'U') IS NULL
BEGIN
    CREATE TABLE dyn.DataSourceProfile
    (
        DataSourceId nvarchar(128) NOT NULL CONSTRAINT PK_dyn_DataSourceProfile PRIMARY KEY,
        DisplayName nvarchar(200) NOT NULL,
        Provider nvarchar(50) NOT NULL,
        ConfigKey nvarchar(128) NOT NULL,
        IsEnabled bit NOT NULL CONSTRAINT DF_dyn_DataSourceProfile_IsEnabled DEFAULT (1),
        AllowTextQueries bit NOT NULL CONSTRAINT DF_dyn_DataSourceProfile_AllowTextQueries DEFAULT (0),
        DefaultCommandTimeoutSeconds int NOT NULL CONSTRAINT DF_dyn_DataSourceProfile_Timeout DEFAULT (30),
        MaxRows int NOT NULL CONSTRAINT DF_dyn_DataSourceProfile_MaxRows DEFAULT (50000),
        CreatedUtc datetime2(3) NOT NULL CONSTRAINT DF_dyn_DataSourceProfile_CreatedUtc DEFAULT SYSUTCDATETIME(),
        ModifiedUtc datetime2(3) NOT NULL CONSTRAINT DF_dyn_DataSourceProfile_ModifiedUtc DEFAULT SYSUTCDATETIME(),

        CONSTRAINT CK_dyn_DataSourceProfile_Timeout CHECK (DefaultCommandTimeoutSeconds BETWEEN 1 AND 900),
        CONSTRAINT CK_dyn_DataSourceProfile_MaxRows CHECK (MaxRows BETWEEN 1 AND 1000000)
    );
END;
GO

IF OBJECT_ID(N'dyn.ReportGrant', N'U') IS NULL
BEGIN
    CREATE TABLE dyn.ReportGrant
    (
        ReportId nvarchar(200) NOT NULL,
        PrincipalType nvarchar(50) NOT NULL,
        Principal nvarchar(300) NOT NULL,
        Permission nvarchar(80) NOT NULL,
        CreatedUtc datetime2(3) NOT NULL CONSTRAINT DF_dyn_ReportGrant_CreatedUtc DEFAULT SYSUTCDATETIME(),

        CONSTRAINT PK_dyn_ReportGrant
            PRIMARY KEY (ReportId, PrincipalType, Principal, Permission),
        CONSTRAINT FK_dyn_ReportGrant_Report
            FOREIGN KEY (ReportId) REFERENCES dyn.Report(ReportId)
    );

    CREATE INDEX IX_dyn_ReportGrant_Principal
        ON dyn.ReportGrant(PrincipalType, Principal);
END;
GO

IF OBJECT_ID(N'dyn.AuditEvent', N'U') IS NULL
BEGIN
    CREATE TABLE dyn.AuditEvent
    (
        AuditId bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_dyn_AuditEvent PRIMARY KEY,
        OccurredUtc datetime2(3) NOT NULL CONSTRAINT DF_dyn_AuditEvent_OccurredUtc DEFAULT SYSUTCDATETIME(),
        CorrelationId uniqueidentifier NOT NULL,
        UserName nvarchar(300) NULL,
        EventType nvarchar(100) NOT NULL,
        ReportId nvarchar(200) NULL,
        RevisionId uniqueidentifier NULL,
        DataSourceId nvarchar(128) NULL,
        DatasetId nvarchar(200) NULL,
        DurationMs bigint NULL,
        RowCount bigint NULL,
        Result nvarchar(40) NOT NULL,
        DetailsJson nvarchar(max) NULL
    );

    CREATE INDEX IX_dyn_AuditEvent_OccurredUtc
        ON dyn.AuditEvent(OccurredUtc DESC);

    CREATE INDEX IX_dyn_AuditEvent_Report
        ON dyn.AuditEvent(ReportId, OccurredUtc DESC);

    CREATE INDEX IX_dyn_AuditEvent_Correlation
        ON dyn.AuditEvent(CorrelationId);
END;
GO

IF OBJECT_ID(N'dyn.ReportExecution', N'U') IS NULL
BEGIN
    CREATE TABLE dyn.ReportExecution
    (
        ExecutionId uniqueidentifier NOT NULL CONSTRAINT PK_dyn_ReportExecution PRIMARY KEY,
        CorrelationId uniqueidentifier NOT NULL,
        ReportId nvarchar(200) NOT NULL,
        RevisionId uniqueidentifier NULL,
        UserName nvarchar(300) NULL,
        StartedUtc datetime2(3) NOT NULL,
        CompletedUtc datetime2(3) NULL,
        DurationMs bigint NULL,
        Result nvarchar(40) NOT NULL,
        ErrorCount int NOT NULL CONSTRAINT DF_dyn_ReportExecution_ErrorCount DEFAULT (0)
    );

    CREATE INDEX IX_dyn_ReportExecution_Report_StartedUtc
        ON dyn.ReportExecution(ReportId, StartedUtc DESC);
END;
GO

IF OBJECT_ID(N'dyn.BackgroundJob', N'U') IS NULL
BEGIN
    CREATE TABLE dyn.BackgroundJob
    (
        JobId uniqueidentifier NOT NULL CONSTRAINT PK_dyn_BackgroundJob PRIMARY KEY,
        JobType nvarchar(80) NOT NULL,
        ReportId nvarchar(200) NULL,
        RevisionId uniqueidentifier NULL,
        RequestedBy nvarchar(300) NULL,
        ParametersJson nvarchar(max) NULL,
        State nvarchar(40) NOT NULL,
        CreatedUtc datetime2(3) NOT NULL CONSTRAINT DF_dyn_BackgroundJob_CreatedUtc DEFAULT SYSUTCDATETIME(),
        StartedUtc datetime2(3) NULL,
        CompletedUtc datetime2(3) NULL,
        LeaseOwner nvarchar(200) NULL,
        LeaseUntilUtc datetime2(3) NULL,
        Attempts int NOT NULL CONSTRAINT DF_dyn_BackgroundJob_Attempts DEFAULT (0),
        Progress decimal(5,2) NULL,
        ResultLocation nvarchar(1000) NULL,
        LastError nvarchar(4000) NULL
    );

    CREATE INDEX IX_dyn_BackgroundJob_State_CreatedUtc
        ON dyn.BackgroundJob(State, CreatedUtc);
END;
GO

IF NOT EXISTS (SELECT 1 FROM dyn.SchemaVersion WHERE VersionNumber = 1)
BEGIN
    INSERT INTO dyn.SchemaVersion(VersionNumber, Description)
    VALUES (1, N'Initial DynReport catalog, immutable revisions, datasource profiles, audit and jobs');
END;
GO
