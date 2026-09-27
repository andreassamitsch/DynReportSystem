/*
 TEMPLATE ONLY - review names before execution.

 Goal:
 - dedicated Windows identity for DynReport
 - no db_owner/sysadmin
 - only SELECT on explicitly exposed reporting schemas/views
 - only EXECUTE on explicitly approved stored procedures

 Replace DOMAIN\DynReportSvc$ and database/schema names with your environment.
*/

-- Example in each source database:
-- CREATE USER [DOMAIN\DynReportSvc$] FROM WINDOWS;
-- GO

-- Preferred: expose a dedicated reporting schema and grant only that surface.
-- GRANT SELECT ON SCHEMA::reporting TO [DOMAIN\DynReportSvc$];

-- If approved stored procedures are used:
-- GRANT EXECUTE ON OBJECT::reporting.usp_ProductionOverview TO [DOMAIN\DynReportSvc$];

-- Explicitly do NOT add the identity to:
--   sysadmin
--   db_owner
--   db_datawriter
-- and do not grant broad ALTER/CONTROL permissions.

-- Metadata database runtime example:
-- USE [DynReport];
-- CREATE USER [DOMAIN\DynReportSvc$] FROM WINDOWS;
-- During the 0.7 transition the application writes catalog/revision/audit
-- metadata directly. Grant only DML on the dedicated DynReport metadata schema:
-- GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::dyn TO [DOMAIN\DynReportSvc$];
--
-- This DML grant applies only to the isolated DynReport metadata database.
-- Source systems (Syncos/Oxaion/etc.) remain read/execute only.
-- A later hardening step can replace direct metadata DML with narrowly scoped
-- stored procedures. Revision cleanup/retention should still be a separate
-- maintenance job.
