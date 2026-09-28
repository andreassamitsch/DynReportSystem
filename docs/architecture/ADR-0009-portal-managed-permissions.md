# ADR-0009: Portal-managed reporting permissions

- **Status:** Accepted
- **Date:** 2026-09-28
- **Decision owner:** Fuchshofer / DynReport System
- **Applies to:** reporting portal, folders, reports, Windows/AD authorization and package imports

## Context

DynReport already enforces explicit allow-only permissions and folder inheritance. Permission sources currently include:

1. bootstrap configuration
2. imported SSRS migration ACLs
3. native `.dynreport` manifest grants

These sources are useful defaults, but day-to-day authorization must be manageable directly in the reporting portal without editing JSON files or rebuilding report packages.

Portal-managed AD assignments are deployment-specific. They therefore must survive application upgrades and package replacements without becoming coupled to the portable report definition.

## Decision

DynReport adds a portal-managed permission override layer.

Evaluation order:

1. bootstrap catalog
2. SSRS migration catalog
3. native package metadata and package grants
4. **portal-managed overrides**

The portal override is authoritative for the direct grants of a target when an override exists.

Folder inheritance remains unchanged:

- direct report grants apply to the report
- direct folder grants apply to the folder
- folder grants are inherited by descendant folders and reports
- there is no implicit access and no deny rule in the current model

## Storage

Portal overrides are stored outside the installation directory:

`C:\ProgramData\DynReportSystem\Config\permissions-overrides.json`

The application pool receives Modify permission only on the dedicated DynReport ProgramData areas it must mutate.

Writes are:

- serialized
- normalized
- written through a temporary file
- backed up to `.bak`
- atomically moved into place where supported by the filesystem

The installer must preserve this directory.

## Administration authorization

A user may edit a target only when that user currently has an effective `Manage` permission for the target.

For folders, `Manage` may be inherited from an ancestor folder.

A folder manager can therefore administer descendants without receiving broad server administration rights.

The portal administration UI itself is visible only to users who can manage at least one report or folder.

## Supported grants

Principals:

- User
- Group
- WindowsPrincipal

Permissions:

- View
- Run
- EditLayout
- Edit
- Publish
- Manage

The runtime continues to use Windows SSO and `ClaimsPrincipal.IsInRole` for Windows group/principal checks.

## Source versus override

When no portal override exists, DynReport uses the direct rights provided by the underlying source.

When a portal administrator saves a target, the complete direct grant list for that target becomes the portal override.

The UI provides **Quellrechte wiederherstellen**, which removes the override and immediately re-exposes the current bootstrap/SSRS/package source rights.

Inherited rights are displayed read-only and are not copied into the direct override.

## Portability

Portal overrides are intentionally **not** written back into a `.dynreport` package.

The report package remains portable. Deployment-specific AD assignments remain with the DynReport installation.

A package import/revision restore therefore cannot silently overwrite portal-managed production access.

## Audit

Permission save/reset operations are written to the existing DynReport audit stream when the metadata store is available.

Audit details include:

- target type
- target id
- acting Windows user
- resulting direct grants for save operations

## Backup

The ProgramData Config directory is part of DynReport disaster-recovery scope.

The portal permission file and its backup must be restored together with the metadata database, packages/revisions and production configuration.

## Consequences

### Positive

- no manual JSON editing for routine access management
- folder inheritance is visible and manageable
- report package updates cannot overwrite production ACL decisions
- least-privilege managers can administer only their own reporting subtree
- permissions survive application upgrades

### Limitations

- current ACL semantics are allow-only; explicit deny is deliberately not introduced
- AD directory browsing/search is not required for the first version; administrators enter the Windows user/group name
- removing the final effective Manage grant can remove the administrator's own ability to edit that target; the UI warns before this class of change
