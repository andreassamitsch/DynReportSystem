/*
 DynReport metadata schema v2 hotfix
 Safe to run against:
 - a partially-created 0.7.1 metadata database
 - a complete 0.7.1 metadata database
 - a fresh database already created with the corrected 001 bootstrap

 Fixes:
 - ROWCOUNT keyword collision prevented dyn.AuditEvent creation.
 - ReportGrant used a potentially 1260-byte clustered primary key; SQL Server's
   clustered index key limit is 900 bytes. Move clustering to a bigint surrogate
   key and keep logical uniqueness in a nonclustered unique index.

 This migration is idempotent.
*/
SET XACT_ABORT ON;
GO

BEGIN TRY
    BEGIN TRANSACTION;

    IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'dyn')
        THROW 50001, 'DynReport schema dyn is missing. Run 001-dynreport-metadata-schema.sql first.', 1;

    IF OBJECT_ID(N'dyn.Report', N'U') IS NULL
       OR OBJECT_ID(N'dyn.ReportGrant', N'U') IS NULL
       OR OBJECT_ID(N'dyn.ReportRevision', N'U') IS NULL
        THROW 50002, 'DynReport core metadata tables are missing. Run 001-dynreport-metadata-schema.sql first.', 1;

    IF OBJECT_ID(N'dyn.SchemaVersion', N'U') IS NULL
    BEGIN
        CREATE TABLE dyn.SchemaVersion
        (
            VersionNumber int NOT NULL CONSTRAINT PK_dyn_SchemaVersion PRIMARY KEY,
            AppliedUtc datetime2(3) NOT NULL CONSTRAINT DF_dyn_SchemaVersion_AppliedUtc DEFAULT SYSUTCDATETIME(),
            Description nvarchar(300) NOT NULL
        );
    END;

    IF OBJECT_ID(N'dyn.ReportGrant', N'U') IS NOT NULL
    BEGIN
        IF COL_LENGTH(N'dyn.ReportGrant', N'ReportGrantId') IS NULL
        BEGIN
            ALTER TABLE dyn.ReportGrant
                ADD ReportGrantId bigint IDENTITY(1,1) NOT NULL;
        END;

        IF EXISTS
        (
            SELECT 1
            FROM sys.key_constraints
            WHERE [type] = N'PK'
              AND [name] = N'PK_dyn_ReportGrant'
              AND parent_object_id = OBJECT_ID(N'dyn.ReportGrant')
        )
        BEGIN
            ALTER TABLE dyn.ReportGrant
                DROP CONSTRAINT PK_dyn_ReportGrant;
        END;

        IF NOT EXISTS
        (
            SELECT 1
            FROM sys.key_constraints kc
            INNER JOIN sys.index_columns ic
                ON ic.object_id = kc.parent_object_id
               AND ic.index_id = kc.unique_index_id
               AND ic.key_ordinal = 1
            INNER JOIN sys.columns c
                ON c.object_id = ic.object_id
               AND c.column_id = ic.column_id
            WHERE kc.[type] = N'PK'
              AND kc.parent_object_id = OBJECT_ID(N'dyn.ReportGrant')
              AND c.[name] = N'ReportGrantId'
        )
        BEGIN
            ALTER TABLE dyn.ReportGrant
                ADD CONSTRAINT PK_dyn_ReportGrant
                PRIMARY KEY CLUSTERED (ReportGrantId);
        END;

        IF NOT EXISTS
        (
            SELECT 1
            FROM sys.indexes
            WHERE object_id = OBJECT_ID(N'dyn.ReportGrant')
              AND [name] = N'UX_dyn_ReportGrant_Report_Principal_Permission'
        )
        BEGIN
            CREATE UNIQUE NONCLUSTERED INDEX UX_dyn_ReportGrant_Report_Principal_Permission
                ON dyn.ReportGrant(ReportId, PrincipalType, Principal, Permission);
        END;

        IF NOT EXISTS
        (
            SELECT 1
            FROM sys.indexes
            WHERE object_id = OBJECT_ID(N'dyn.ReportGrant')
              AND [name] = N'IX_dyn_ReportGrant_Principal'
        )
        BEGIN
            CREATE INDEX IX_dyn_ReportGrant_Principal
                ON dyn.ReportGrant(PrincipalType, Principal);
        END;
    END;

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
            [RowCount] bigint NULL,
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

    IF NOT EXISTS (SELECT 1 FROM dyn.SchemaVersion WHERE VersionNumber = 2)
    BEGIN
        INSERT INTO dyn.SchemaVersion(VersionNumber, Description)
        VALUES
        (
            2,
            N'Fix audit ROWCOUNT syntax and ReportGrant clustered key width'
        );
    END;

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0
        ROLLBACK TRANSACTION;
    THROW;
END CATCH;
GO
