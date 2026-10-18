# Contributing to Draughtsman

Thank you for wanting to help. Draughtsman is free software under the GNU AGPL-3.0-only, maintained by Overpass Limited.
Bug reports, documentation fixes, ideas and pull requests are all welcome.

By taking part you agree to follow the [Code of Conduct](CODE_OF_CONDUCT.md).

## Before you start

- **Questions and ideas**: use [GitHub Discussions](https://github.com/overpassapps/draughtsman/discussions), not issues.
- **Bugs**: open an issue with the bug report form. A short DSL snippet that reproduces the problem is the best thing you can
  attach.
- **Security problems**: do **not** open an issue. Follow [`SECURITY.md`](SECURITY.md) and write to
  draughtsman@overpass.co.uk.
- **Larger changes**: open an issue (or a discussion) first and agree the approach. It saves you writing something we cannot
  take. The "Not built yet" list in the [README](README.md) shows what is known to be missing.

## Contributor Licence Agreement

Overpass Limited offers Draughtsman under the AGPL and, to those who need it, under a commercial licence. To keep that
possible, **every outside contribution needs a signed-off [Contributor Licence Agreement](CLA.md)**. You keep your copyright;
you grant Overpass Limited a licence that includes the right to relicense. Read [`CLA.md`](CLA.md), then comment
`I have read the CLA and I agree.` on your first pull request (or tick the box in the pull request template). There is no bot:
a maintainer checks. We cannot merge a pull request until you have agreed. (The CLA is currently a template awaiting legal
review; see the note at its top.)

## Licence and file headers

The whole repository is **GNU AGPL-3.0-only**, copyright Overpass Limited ([`LICENSE`](LICENSE)). You do not need to add a
licence header to files. A new source file may start with `SPDX-License-Identifier: AGPL-3.0-only` if you like. Do not copy in
code under a licence that is incompatible with the AGPL-3.0, and do not add a dependency without saying so in your pull
request and why; `THIRD-PARTY-NOTICES.txt` is generated (`node scripts/generate-notices.mjs`) and a dependency whose licence is not on
its allow-list stops the run until a maintainer has reviewed it.

## Building, running and testing

Prerequisites: the .NET SDK pinned in `global.json` (10.0.x) and Node 22.12 or later. Linux, macOS and Windows all work for
development.

```sh
# Backend. `dotnet build` also runs `npm ci` and bundles the sidecar into the server's output.
dotnet build
dotnet test

# Web app (Vite, React 18, TypeScript)
cd web
npm ci
npm run lint
npm run build        # writes web/dist, which the server serves when it exists
npm test

# Layout and Yjs sidecar (Node)
cd sidecar
npm ci
npm test
```

Run the server from the repository root:

```sh
dotnet run --project src/Draughtsman.Server        # http://localhost:5180, health at /api/health
cd web && npm run dev                              # http://localhost:5173, proxies /api to 5180
```

The first start prints a one-time setup code in the log; use it to create the first administrator. The server keeps its data in
`./data` (or `DRAUGHTSMAN_DATA`).

### Before you open a pull request

Run **`scripts/verify-local.sh`**. It runs the same steps as CI, in order: `dotnet restore`, `build`, `test` and
`format --verify-no-changes`, then lint, build and tests for `web/` and `sidecar/`, and prints a PASS or FAIL line per step
with full logs kept in a temporary folder. Paste the summary in your pull request. CI itself runs the Ubuntu and Windows legs
on pushes to `main`.

Notes that save time:

- Warnings are errors on the .NET side (`Directory.Build.props`), and ESLint runs strict, type-checked rules on the web side.
- A storage change must pass the shared suite in `tests/Draughtsman.Storage.Tests/StoreContractTests.cs` for both providers;
  Postgres tests run when `DRAUGHTSMAN_TEST_POSTGRES` is set and are skipped otherwise.
- A test must not assert an absolute wall-clock time on CPU-bound work: assert a count or an outcome. `AGENTS.md` explains why.

## Coding standards and conventions

[`AGENTS.md`](AGENTS.md) is the contributors' guide to how the code is organised and the rules that are not obvious from it
(it is also written for AI coding agents, and a human reader will find it just as useful). In particular:

- The **build specification** [`docs/spec/build-spec-v0.1.md`](docs/spec/build-spec-v0.1.md), including its Amendments, is the source of
  truth for behaviour. Read it before a change of substance. The decisions behind the architecture are in [`docs/adr/`](docs/adr/).
- The **document model lives in four places** (C# models, the JSON schema, the web types and the sidecar types) and they must
  change together, with the shared example and its DSL golden file updated.
- **UI work needs a design atlas entry first**: a note in `design/atlas/<screen>.md` (see
  [`design/atlas/README.md`](design/atlas/README.md)), cited in the pull request.
- Keep the public surface documented: every HTTP endpoint is in [`docs/api.md`](docs/api.md), and every setting is in
  [`docs/draughtsman.example.yaml`](docs/draughtsman.example.yaml).
- No Windows-only APIs (ADR 0001).
- British English in documentation and product copy.
- Commit messages: short, imperative, and the reason rather than the restatement of the diff.
- Keep a change small and focused. Match the style of the code around it.

## Pull requests

1. Fork the repository and branch from `main`.
2. Make the change, with tests that fail without it.
3. Run `scripts/verify-local.sh`.
4. Add a line under **Unreleased** in [`CHANGELOG.md`](CHANGELOG.md) for anything a user would notice.
5. Open the pull request using the template, and agree to the [CLA](CLA.md) if this is your first.

A maintainer will review it. We may ask for changes, or decline something that does not fit the product; we will say why.
