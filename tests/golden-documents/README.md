# Golden documents

Documents written by EARLIER builds, copied byte for byte out of git history, that the current build must still load, validate against today's
schema, print as DSL, parse back, render and re-save without changing what they say (`tests/Draughtsman.Mcp.Tests/GoldenDocumentTests.cs`).

| Directory | Source | Files |
| --- | --- | --- |
| `m0-8d3b6bd/` | `8d3b6bd`, the first scaffold commit (M0): `git show 8d3b6bd:docs/examples/order-service.json` | the example document at `schemaVersion` 1, before storage existed |
| `first-push-9e22f56/` | `9e22f56`, the first commit pushed: `git show 9e22f56:docs/examples/<file>` | `order-service.json` and the `.dsl` that build printed for it, `editor-session.json` (a node of every vocabulary kind) |

The `.dsl` is what THAT build's printer wrote, so the test also proves the printer has not drifted: a change here means every saved diagram's text
export changes, and any DSL a customer keeps in version control with it. These files are never edited; a release adds a directory only if its document
format changed in a way an older document must survive (`docs/upgrade-backup.md`, "What a patch may and may not change"). The upgrade fixtures
(`tests/upgrade-fixtures/`) cover documents of every type that real earlier builds stored.
