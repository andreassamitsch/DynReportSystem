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
- verify package rollback

## Values still to be agreed

- RPO metadata
- RPO package revisions
- RTO DynReport
- RTO SQL
- audit retention
- report execution history retention
