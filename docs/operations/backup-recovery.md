# DynReport backup and recovery baseline

## Components to protect

1. DynReport metadata SQL database
2. Published report revisions / package storage
3. APP-01 production configuration
4. data-source credential/secret recovery procedure
5. TLS certificates required by IIS/SQL validation
6. audit retention store
7. Git repository / CI artifacts

## Minimum operational policy

- metadata DB: SQL full backup plus differential/log strategy according to agreed RPO
- package revisions: immutable and backed up independently
- configuration: backed up without exposing secrets in Git
- restore test: scheduled and documented
- rollback: activation pointer can return to the preceding validated report revision
- installer deployment: preserve production configuration and report storage

## Report revision rollback in 0.8.0

The in-application rollback is for a **single native report revision**. It does not replace a full
SQL/configuration disaster-recovery restore.

Procedure:

1. open the native report in the DynReport Designer
2. open **Versionen**
3. verify that the SQL revision catalog is available
4. choose a non-active validated revision
5. click **Wiederherstellen**
6. confirm with **Wirklich wiederherstellen**
7. reopen/run the report and validate business values
8. verify the active revision and audit event in the DynReport metadata database

Only users with `Publish` or `Manage` permission can perform this action.

Before activation DynReport verifies package length and SHA-256 and validates the package with the
current runtime. SQL report metadata, grants and the active revision pointer are changed in one
transaction. If that SQL transaction fails after the local package was replaced, DynReport attempts
to restore the previous local package automatically.

Designer saves, package imports and revision restores now compensate the local package if the
required SQL metadata mutation fails. The filesystem and SQL catalog are still separate durable
stores, so infrastructure-level disaster recovery must continue to protect both. The successful
APP-01 rollback validation is the baseline for the later cutover to an SQL-authoritative catalog.

## Restore test checklist

- restore metadata DB to isolated target
- restore package/revision storage
- restore or re-bind required certificates
- restore secrets through the approved secret recovery mechanism
- start DynReport against restored metadata
- open portal
- execute at least one Oxaion and one Syncos report
- verify Windows SSO and report permissions
- verify audit writes
- restore an older native report revision through the designer
- verify package hash/length validation succeeded
- verify ActiveRevisionId, report grants and active package agree after rollback
- verify a failed/aborted rollback leaves the previous report usable

## Values still to be agreed

- RPO metadata
- RPO package revisions
- RTO DynReport
- RTO SQL
- audit retention
- report execution history retention
