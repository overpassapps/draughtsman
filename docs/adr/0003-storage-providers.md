# ADR 0003: Storage providers

**Status:** Accepted, 2026-09-20

## Context

Spec section 7 puts EF Core behind one `IStore` interface with three providers (SQLite, Postgres, SQL Server) and requires a shared integration test suite that runs every test against every provider in CI. The maintainer has amended this: SQL Server is deferred to phase 2 as a Windows-installation option, and the MVP ships SQLite plus Postgres. The `IStore` interface is in M0; the shared provider test suite lands with the storage task. Together they are what make adding SQL Server later a driver rather than a rewrite.

The storage abstraction is one of the four things spec section 3 marks non-negotiable for the MVP.

## Decision

1. **Providers.**
   - **SQLite** is the default. Zero setup; the database file is `data.db` inside the data folder; backup is a copy of the folder.
   - **Postgres** ships in the MVP, selected by connection string.
   - **SQL Server** is phase 2, offered for Windows installations whose DBA already runs and backs it up. Nothing in the interface or schema may assume it will never exist.
2. **Selection** is by `storage.provider` (`sqlite` | `postgres`, later `sqlserver`) and `storage.connectionString` in `draughtsman.yaml`. An absent `storage` section means SQLite at `<data>/data.db`.
3. **EF Core behind `IStore`.** `IStore` lives in `Draughtsman.Core` and is the only storage surface the server, the sync hub and the MCP layer see. This skeleton ships the interface with an in-memory implementation registered in DI; the storage task replaces that registration with an EF Core implementation in its own project (`Draughtsman.Storage.EfCore` or similar) and per-provider migration assemblies. No caller may reference a `DbContext` directly.
4. **Tables** (spec section 7): `documents`, `document_updates`, `document_versions`, `assets`, `users`, `workspaces`, `memberships`, `themes`, `audit`. Yjs updates in `document_updates` are stored as opaque byte arrays (see ADR 0002). The JSON snapshot column on `documents` is regenerated on every save. Asset bytes live on disk under the data folder; the row holds path and hash only.
5. **Shared provider test suite.** One abstract xUnit test class exercises the whole `IStore` contract; a concrete subclass per provider supplies the connection. SQLite runs everywhere, always. Postgres runs against a service container on the `ubuntu-latest` CI leg and is skipped on other legs unless `DRAUGHTSMAN_TEST_POSTGRES` provides a connection string. Adding SQL Server later means one subclass and one CI leg.
6. **Lowest common denominator by default.** Anything provider-specific (full-text search, JSON columns, JSON path queries) sits behind the interface with a portable fallback (for example a `LIKE`-based search over title and a denormalised text column). A provider may override with a faster native path, but the shared test suite must pass on the fallback.
7. **Migrations** run at startup. For SQLite the server takes a copy of `data.db` next to it before applying any migration; Postgres (and later SQL Server) users are told in the upgrade notes to snapshot first. Downgrade is restoring the copy; the copy's name and the restore procedure are in `docs/api.md`.

## Alternatives considered

- **Dapper or hand-written SQL per provider.** Rejected: three dialects means three times the surface to write, test and keep in step; EF Core's migrations and provider model already solve the portability problem for the schema this product needs.
- **A document store (LiteDB, RavenDB).** Rejected: the on-prem operator's DBA runs Postgres or SQL Server and will not adopt a new database for one product. SQLite already covers the zero-setup case.
- **SQL Server in the MVP as the spec first wrote it.** Rejected by the maintainer: it costs a Windows CI leg with a SQL Server container and a third migration assembly before any customer has asked for it. The abstraction plus shared test suite keeps the cost of adding it later to a driver.
- **Storing the JSON snapshot only, no update log.** Rejected: the update log is what makes realtime sync, checkpoints before agent edits and semantic diff between versions possible (spec sections 4, 7 and 9).

## Consequences

- The storage task must keep `IStore` free of EF Core types; DTOs and the document model records in `Draughtsman.Core` are the contract.
- CI's `ubuntu-latest` leg grows a Postgres service container in the storage task; the other legs stay fast.
- The test suite is the specification of provider behaviour. Any provider-specific optimisation lands with a test that also passes on the fallback.
- SQLite's single-writer nature is acceptable for the MVP (single editor) and phase 2 realtime on small teams; installs that outgrow it move to Postgres by changing two config keys and running an export/import, which the storage task should provide as a CLI command.
