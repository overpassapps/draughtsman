# Schema validator corpus

`DocumentSchema.Validate` is the one gate every document passes (the REST API validates every body, the MCP tools every patch and every created
document). It used to be JsonSchema.Net; it is now `SchemaValidator` in Core ([ADR 0004](../../../docs/adr/0004-document-schema-validator.md)). This
folder is the proof that the replacement says what the library said: the same verdict, the same violation lines in the same order, for every text
below, recorded from the library **before** it was removed.

`SchemaCorpusTests.Every_case_has_the_outcome_that_was_recorded_for_it` builds the cases (deterministically) and compares what
`DocumentSchema.Validate` does now with `expectations.jsonl`, line by line.

## What is in it

| Part | Where | What |
| --- | --- | --- |
| Seeds | `seeds/*.json` | Frozen copies of valid documents: every `docs/examples` document except the 2,000-node benchmark, the golden documents earlier builds wrote (`tests/golden-documents`), and the distinct documents and versions in the data folders of the seven upgrade fixtures (real builds, filled through their own API). Each is a case twice: as written, and compacted. |
| Harvest | `harvested.jsonl` | Every JSON text any test in this repo handed the validator (the Core, MCP and server suites, run with a temporary hook in `DocumentSchema.Validate`): importer outputs, REST and MCP bodies, the invalid variants those tests make. 181 documents, deduplicated by shape. One JSON string literal per line. |
| Probes | `Handwritten.cs` | Directed cases for what validators differ on: regular-expression anchors and character classes (trailing newlines, fullwidth and Arabic digits, `\r\n`), text length as UTF-16 units, code points or grapheme clusters, number spellings (`10e-1`, `1.0`, `1e400`, 36 digits), RFC 3339 corners (leap seconds, offsets, `_` and space as separators, year `0000`), duplicate and escaped member names, JSON pointer escaping in locations, lone surrogates, 1-2 MiB strings, 3,000-node and 20,000-key documents, nesting 5 to 2,000 deep, and text that is not JSON. |
| Mutations | `Mutations.cs` | Every seed with one or two things changed: at each node class the member dropped, retyped 13 ways, given 38 hostile strings or 44 hostile numbers, an unknown member added under 11 names, arrays emptied, grown, duplicated and appended to; plus a seeded pseudo-random sample of single and double mutations per seed. The generator knows JSON trees and nothing about the schema. |
| Outcomes | `expectations.jsonl` | `{"id", "sha", "o", "v"?, "e"?}` per case: the fingerprint of the exact text, `valid`, `invalid` with the violation lines verbatim and in order, or `throws` with the exception type. |

`SchemaCorpusTests` also fails if the corpus shrinks below its volume, if it stops having every kind of outcome, or if a keyword of the schema
stops being exercised by recorded violations.

## Rules

- **Never edit `expectations.jsonl` by hand.** It was recorded by the JsonSchema.Net 9.4.0 build of `DocumentSchema` (the commit before the library was
  removed: `git log --oneline -- tests/Draughtsman.Core.Tests/SchemaCorpus/expectations.jsonl`). To record it again from that commit:
  `DRAUGHTSMAN_RECORD_SCHEMA_CORPUS=1 dotnet test tests/Draughtsman.Core.Tests --filter Every_case_has_the_outcome` and check `git diff` is empty (it is
  byte-identical on repeated runs: nothing here depends on the clock, the machine or the runtime's `Random`).
- **Recording from a later commit records the replacement's own behaviour**, not the library's. The only legitimate reason to do it is a deliberate change
  to the schema or to what the validator says; then review the whole diff, and say in the commit what changed and why.
- The cases the replacement deliberately answers differently are listed, with the reason and the exact new outcome, in `intentional-differences.jsonl`.
  A difference passes only if a rule in `IntentionalDifferences.cs` names it and it is listed; every listed entry must still differ (a stale one fails);
  every other case must match exactly. `DRAUGHTSMAN_RECORD_SCHEMA_CORPUS=differences` rewrites the list from the current behaviour and fails on a difference
  no rule explains; `DRAUGHTSMAN_SCHEMA_CORPUS_SHOW=500` lists more of the differing cases than the default 12.
- `BROKEN-ON-PURPOSE.md` lists what was broken in the validator to show a test fails for each keyword and judgement.
- **Every text the generator builds must be byte-identical on every host**, because the fingerprint is of the exact text. Anything that writes text
  with a line break must say `\n`: `JsonSerializerOptions.NewLine` (and `StringBuilder.AppendLine`) default to `Environment.NewLine`, which is how
  `syntax|document-pretty` was built with CRLF on the Windows CI leg and failed there alone. Seeds are read with `\r\n` folded to `\n` and
  `.gitattributes` checks every file out as LF, so a checkout never changes them.
- A change to the generator or to a seed changes the fingerprints, which fails the run: that is the point. Re-record only when the library is still the
  validator, or accept that the recording is then the replacement's.
