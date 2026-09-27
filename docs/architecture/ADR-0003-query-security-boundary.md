# ADR-0003: Query security boundary

- **Status:** Accepted
- **Date:** 2026-09-27

## Decision

Report SQL is privileged content. A report designer must never become an unrestricted SQL
console.

Security is enforced in layers:

1. **Database identity is the primary boundary.** DynReport uses dedicated read-only /
   execute-only principals with least privilege.
2. **Logical DataSource profiles are centrally configured.** Packages reference IDs such
   as `SyncosPRD`, not credentials.
3. **All end-user values are typed SQL parameters.**
4. **Text-query policy validation is defense in depth.** Write/DDL/admin constructs and
   multi-batch commands are rejected before execution.
5. **Stored procedures are allowlisted by the permissions of the DynReport SQL identity.**
6. **Dataset editing and publishing are separate permissions.**
7. **Every publish and execution is auditable.**

## Designer permissions

The authorization model distinguishes:

- View
- Run
- Export
- EditLayout
- EditExpressions
- EditDataset
- Publish
- ManagePermissions
- ManageDataSources

`EditDataset` and `Publish` are privileged roles.

## Explicitly forbidden in a report package

- arbitrary C#, VB, JavaScript, PowerShell or shell execution
- reflection / assembly loading
- filesystem access from expressions
- network calls from expressions
- embedded credentials
- SQL connection strings containing credentials
- SQL batches containing destructive/administrative statements

## Important limitation

Static SQL inspection is **not** the trust boundary. It can miss complex SQL constructs.
The dedicated SQL principal with least privilege remains mandatory even when policy
validation passes.
