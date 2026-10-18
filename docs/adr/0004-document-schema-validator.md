# ADR 0004: The document schema validator

**Status:** Accepted, 2026-10-01

## Context

Every document the product accepts is checked against `docs/schema/document.schema.json` (JSON Schema draft 2020-12): the REST API validates every body before it reaches the store, and the MCP tools validate every created document and every patched result. Until now one library did that, JsonSchema.Net 9.4.0 (json-everything, Greg Dennis), in exactly one place, `DocumentSchema.Validate` in `Draughtsman.Core`.

JsonSchema.Net and the two packages it needs, JsonPointer.Net and Json.More.Net, are distributed under the **Open Source Maintenance Fee** EULA (`OSMFEULA.txt` in each nupkg, named by the nuspec's `<license type="file">`). The source is MIT, but the compiled **Binary Release** is offered "in exchange for" a monthly fee from any user who uses it in revenue-generating activities with annual gross revenue of US$10,000 or more; a user may compile the MIT source instead and owe nothing. Draughtsman is sold to organisations and ships the compiled library inside the archive and the Docker image, so the obligation would not stop at Overpass: it would travel with the product to every customer (`THIRD-PARTY-NOTICES.txt` carried the full EULA for these three packages and `scripts/generate-notices.mjs` flagged them, "sale-readiness audit LG-04, awaiting the maintainer (Q2)"). The maintainer's decision on 2026-10-01: find a way to stop depending on it.

The requirement that makes this more than a package swap is that **`Validate` is a contract**, not an implementation detail: the 400 body of `PUT /api/documents/{id}` and the MCP `The document is not valid: ...` message carry its `location: message` lines to clients and agents, and a document that validates today must keep validating (and one that is refused must keep being refused) or a stored diagram stops opening or a bad one gets in.

## What the schema actually uses

`node scripts/schema-keywords.mjs` over the committed schema (104 subschemas):

| Keyword | Uses | Notes |
| --- | --- | --- |
| `type` | 69 | `string` 24, `number` 18, `object` 17, `array` 7, `boolean` 2, `integer` 1 (`schemaVersion`) |
| `$ref` | 35 | all to `#/$defs/<name>`, 21 distinct targets, no remote or anchor references |
| `properties` / `required` / `additionalProperties` | 15 / 13 / 17 | `additionalProperties` is `false` 16 times, `true` once (`props`), a schema once (`theme`: strings) |
| `minimum` / `maximum` / `exclusiveMinimum` | 9 / 4 / 2 | |
| `items` | 7 | one schema for every element |
| `enum` | 6 | 3 to 14 strings each |
| `pattern` | 4 | `^...$` over ASCII classes: id, shape, colour, port reference |
| `format` | 3 | only `date-time` |
| `maxLength` / `minLength` / `minItems` / `maxItems` / `const` | 3 / 2 / 1 / 1 / 1 | `const` is `1`, on `schemaVersion` |
| `$defs`, `$id`, `$schema`, `title`, `description`, `default` | | annotations and definitions |

No `oneOf`/`anyOf`/`allOf`/`not`, no `if`/`then`/`else`, no `unevaluated*`, no `patternProperties`, no `prefixItems`, no dynamic or recursive references, no `uniqueItems`. The whole contract is the 18 keywords above.

## What the library does that the contract now depends on

Found by recording it (see Verification): behaviours of JsonSchema.Net 9.4.0 that a replacement must reproduce or consciously drop. They are not claims about what JSON Schema says; they are *what ships today*, which is what stored documents and clients have been judged by.

- **Regular expressions** are .NET `Regex` with `RegexOptions.ECMAScript` (5 s timeout), so `$` also matches before a final `\n`: the id `"n_1\n"` is valid.
- **`maxLength`/`minLength` count grapheme clusters** (`StringInfo.LengthInTextElements`), not UTF-16 units or code points: sixty `e` + combining acute accents are 60 characters, a family emoji is one.
- **`type: integer`** is decided from the number's text and says no to `10e-1` and `100e-2` (no decimal point, negative exponent) although both are 1, and yes to `1.0`, `0.1e1`, `1.0e-0`.
- **`const`/`enum` compare numbers as `decimal`**: `1.0000000000000000000000000000000000001` equals `1` (it rounds), and a number outside the range of `decimal` (`1e30`, 30 digits) makes `Validate` **throw `FormatException`**, a 500 from the API.
- **`minimum`/`maximum`/`exclusiveMinimum`** compare the numbers' text exactly, to any length.
- **`format: date-time`** is RFC 3339 with `T`, `t`, `_` or a space between date and time, `Z` or `z`, any number of fraction digits, a leap second only at 23:59:60 UTC (offset applied), offsets up to `+23:59`, year `0000`, a final `\n` tolerated, and non-ASCII digits refused.
- **Duplicate member names**: `properties` validates the **first** occurrence of a member and ignores the rest; `additionalProperties` sees every occurrence. System.Text.Json deserialises the **last**. So `{"id": "d1", "id": "1x"}` validates (the pattern is never applied to `"1x"`) and then stores the id `1x`. This is a pre-existing gap, not closed here (closing it changes verdicts); see Follow-ups.
- **A lone surrogate** (`"\ud800"`, legal JSON, not legal text) makes `Validate` throw `InvalidOperationException` at any value read as text (a pattern, a length, an enum or a format) and wherever a member's *name* is read (every object that has an `additionalProperties`), and is accepted at a value nothing reads as text (`title`).
- **An exponent of ten or more digits** (`1e1000000000000`) was read into an `int` and wrapped, so that number was "number", not "integer", in a message; the ordering of such numbers against a limit happened to come out right for the spellings tried.
- **Violation lines**: `<JSON pointer, or "(document)">: <message>`, one line per keyword per evaluated node, applicator failures included (`Some properties did not match the required schema: ["title"]` at the parent as well as the child's own line), nodes in breadth-first order of the evaluation tree (a `$ref` is a level), the names in a message in schema order for `properties` and instance order for `additionalProperties`, the whole list de-duplicated.

## Candidates

Evidence is from the packages themselves (nuspec, `lib/`, README) and from running each over the corpus below.

| Candidate | Licence (nuspec) | Fit |
| --- | --- | --- |
| **JsonSchema.Net 9.4.0** (incumbent) | OSMF EULA, with JsonPointer.Net 7.0.2 and Json.More.Net 3.0.1 | Complete; the problem. |
| **Corvus.Json.Validator 5.7.4** | Apache-2.0 | Generates C# types from the schema and **compiles them with Roslyn at run time**: depends on `Microsoft.CodeAnalysis.CSharp` 5.3.0 (23.9 MB package; `Microsoft.CodeAnalysis.Common` 9.3 MB more) and the Corvus code-generation packages. Not compatible with trimming or AOT, adds tens of megabytes to the archive and the image and needs the compiler to start. Rejected on design. |
| **NJsonSchema 11.6.1** | MIT | Reads draft 4 to 7. Run against this schema it does not load: `The schema reference path '#/$defs/id' has not been resolved` on every document (it knows `definitions`, not `$defs`). Pulls in Newtonsoft.Json. Rejected: no draft 2020-12. |
| **LateApexEarlySpeed.Json.Schema 4.2.0** | BSD-3-Clause (as are its `LateApexEarlySpeed.Nullability.Generic` 1.0.4 dependency; `Microsoft.Extensions.Http` 3.1.0 brings the 3.1 Microsoft.Extensions chain) | Draft 2020-12, `netstandard2.1`, 236 KB, says it passes the official suite apart from documented limitations. Run over the 10,274 corpus cases the old validator answered, with format validation on, it **disagrees on 129 (1.3%)**: 76 documents the incumbent accepts it rejects (RFC 3339 timestamps with `t`/`z`, nine fraction digits, `+23:59` or `-23:59` offsets, a leap second) and 53 the incumbent rejects it accepts (the `+0000` offset and other date-time forms, number spellings, other mutations). Its violations would have to be mapped into our `location: message` lines, and every one of those differences adopted, worked around or documented. Viable, but at least four more packages (more through `Microsoft.Extensions.Http`) for 18 keywords, and it moves the verdict instead of keeping it. |

Option (c), validating through the Node sidecar's Ajv, was rejected without a trial: validation must work when the sidecar is unavailable (a REST save must never depend on a child process), and a process hop per document is slower than the thing it replaces.

## Decision

**Write the validator: `SchemaValidator` in `Draughtsman.Core`, no new dependency.** It implements exactly the 18 keywords the schema uses (plus `maximum` and `exclusiveMaximum`, for symmetry with the two that are used), reproduces the behaviours listed above, and **refuses to load a schema that uses a keyword it does not implement**, so the day someone adds `oneOf` to the schema a test fails instead of the keyword being silently ignored (JsonSchema.Net skipped an unknown keyword without a word).

The reasons:

1. The keyword set is small, closed and free of the keywords that make a validator hard (no combinators, no annotation-dependent keywords, no dynamic scope). The hard parts are all *behaviours*, not keywords, and a replacement from outside would have had to be bent to every one of them; written here they are the specification.
2. The only licence this can have is ours: no fee, no usage terms, nothing to carry to a customer and nothing to add to `THIRD-PARTY-NOTICES.txt`.
3. It is smaller than the dependency list it replaces and faster (see Results).
4. It can be proven. A differential corpus recorded from the library before it was removed turns "same verdicts" from a claim into a test, and the official JSON-Schema-Test-Suite cases for every keyword implemented are run as test data, so the implementation is also held to the specification where the library never deviated from it.

## Compatibility policy

`DocumentSchema.Validate(string json)` keeps its signature and its output format. For the whole corpus the replacement gives the library's verdict, the same violation lines in the same order, and the same exception for text that is not JSON. It deliberately differs from the library in exactly four places, each a case where the library **threw or overflowed**; none moves a document the library could judge from invalid to valid:

| Reason | Library | Replacement | Cases |
| --- | --- | --- | --- |
| `decimal-range` | a number outside the range of `decimal` (`schemaVersion: 1e30`, 30 digits) made `Validate` throw `FormatException`, a 500 | `/schemaVersion: Expected "1"` | 26 |
| `lone-surrogate` | a lone surrogate in a value read as text, or in an unknown member's name, threw `InvalidOperationException` | `Value is not valid Unicode text` at the value; the name shown as written (`/\ud800: All values fail against the false schema`) | 70 |
| `lone-surrogate-in-member-name` | a lone surrogate in the name of a member of `props` or `theme` threw | not an error: `props` accepts any member, and `theme` values are checked as before | 14 |
| `exponent-overflow` | `1e1000000000000` was "number" in a message (an `int` wrapped) | "integer" | 1 |

The list is `tests/Draughtsman.Core.Tests/SchemaCorpus/intentional-differences.jsonl`, with the exact new outcome of each case. A difference is accepted only when a rule in `IntentionalDifferences.cs` names it **and** it is listed, and every listed entry must still differ, so re-recording cannot hide a regression that is not one of these four. `DocumentSchema.Validate` now throws for text that is not JSON (`JsonException`) and for nothing else.

## Verification

- **Differential corpus** (`tests/Draughtsman.Core.Tests/SchemaCorpus/`, README there): 10,446 texts recorded from JsonSchema.Net 9.4.0 before it was removed (commit `4d0d100`: 4,133 valid, 6,141 invalid, 172 that throw): 37 example, golden and upgrade-fixture documents (each as written and compacted), 181 documents the repo's own tests validate, 3,324 directed probes and 6,867 mutations. Verdict, violation lines and their order must match exactly; the 111 differences above are the only exceptions. Recording is byte-for-byte reproducible, and the corpus fails if its volume or any keyword's coverage shrinks.
- **Official JSON-Schema-Test-Suite** (MIT, `tests/Draughtsman.Core.Tests/JsonSchemaTestSuite/`): the draft 2020-12 files for every keyword implemented, 436 cases in 109 groups; 52 groups that need a keyword outside the 18, a list of types, a remote reference, a regular expression the .NET engine does not read as ECMAScript or a decimal where an integer is required are skipped by name. Eleven cases keep the library's behaviour instead of the suite's answer (a final line feed before `$` in `date-time`, `\s` and `\S` as ASCII whitespace, a quantifier on half a surrogate pair), each listed with its reason.
- **Keyword guard**: the committed schema must compile, and 23 keywords the validator lacks are each shown to stop a schema loading.
- **Deliberate breakage**: each handler and each judgement (type, integer-ness, required, properties and its first-member rule, additionalProperties and its ordering, items, `$ref` and its level, enum, const, pattern and its options, length, counts, the four bounds, exact and decimal comparison, `date-time` and its leap-second and line-feed rules, de-duplication, level order, pointer escaping, name escaping, the loading guard) was broken on purpose, one at a time, and a named test failed for each: 36 breakages, all caught, with the failing tests in `tests/Draughtsman.Core.Tests/SchemaCorpus/BROKEN-ON-PURPOSE.md`. The same file records the busy loop that fails the two performance tests and the quadratic scan that fails the linear-time test.

## Results

Measured with `SchemaValidationPerformanceTests` in Release on .NET 10, on a Mac running other builds (load average 24 to 38), alternating the old validator (commit `4d0d100`, JsonSchema.Net 9.4.0) and the replacement for three rounds from clean clones. `DocumentSchema.Validate(string)` is what the document API calls for every body, so the times include parsing the JSON text.

| | JsonSchema.Net 9.4.0 | `SchemaValidator` |
| --- | --- | --- |
| The 2,000-node, 2,500-edge benchmark document (799 KiB), one body | fastest 75 to 98 ms, median 93 to 124 ms | fastest 5.9 to 8.3 ms, median 7.2 to 11.0 ms |
| A stream of 10,000 small documents (3.1 KiB each) | fastest pass 1.63 to 1.83 s (163 to 183 us each), median 1.66 to 2.07 s | fastest pass 0.26 to 0.37 s (26 to 37 us each), median 0.28 to 0.44 s |

About twelve times faster on the large document and five to six times on the stream. The 3.1 KiB documents are now dominated by `JsonDocument.Parse`, which both validators pay.

What leaves the product: `JsonSchema.Net.dll` (294 KB), `Json.More.dll` (54 KB), `JsonPointer.Net.dll` (25 KB) and `Humanizer.dll` (about 675 KB, MIT, which only JsonPointer.Net pulled into the shipped closure) from the archives and the image, plus four entries and the EULA text (115 lines in all) from `THIRD-PARTY-NOTICES.txt`. `dotnet list package --include-transitive` names none of JsonSchema.Net, Json.More.Net and JsonPointer.Net for any project, and none of the four for `Draughtsman.Server`; the one `Humanizer.Core` left in the solution is 2.14.1 under `Draughtsman.Storage`, from the EF Core design-time tooling, which is not shipped. The managed assemblies of a release archive live inside the single-file `draughtsman` executable, so the check is for the names in it: neither the osx-arm64 nor the linux-x64 archive of this change contains `JsonSchema.Net`, `Json.More`, `JsonPointer.Net` or `Humanizer` anywhere (an osx-arm64 archive built from the commit before it contains them, 9 to 11 times each, and the EULA text; the `dotnet publish` the Dockerfile runs lists `JsonSchema.Net.dll`, `Json.More.dll`, `JsonPointer.Net.dll` and `Humanizer.dll` among its 47 files there and 43 files, none of them, here). The osx-arm64 executable is 1.0 MB smaller (132.86 to 131.85 MB) and its archive 517 KB.

## Consequences

- Four packages fewer (JsonSchema.Net, JsonPointer.Net, Json.More.Net and the MIT Humanizer.Core that only JsonPointer.Net pulled in), their entries and the EULA text out of `THIRD-PARTY-NOTICES.txt`, and no build, archive or image of Draughtsman contains a library under the OSMF EULA (`dotnet list package --include-transitive` and a search of an unpacked archive find none). `scripts/generate-notices.mjs` names the three and refuses them if one returns.
- `schema/document.schema.json` stays the contract and `get_schema` is unchanged. Adding a keyword to the schema now means implementing it in `SchemaValidator`, with a corpus and suite entry, in the same change; the compile guard enforces the order.
- The corpus is frozen evidence about the old behaviour. A deliberate change to what the validator says is a change to `expectations.jsonl` with the diff reviewed, not a quiet one.

## Follow-ups (not done here)

- **Duplicate member names.** The library validated the first occurrence and the deserialiser keeps the last, so a document can carry a value the schema would refuse behind a first one it accepts (`{"id": "d1", "id": "1x"}`). Fixing it changes verdicts, which this change was forbidden to do. The sound fix is to refuse duplicate member names in `DocumentSchema.Validate` (and in the importers' JSON paths); `hand:dup|*` in the corpus records today's behaviour.
- Anything the corpus did not think of is the library's behaviour only if the replacement happened to reproduce it, so a report of any difference from what the library said is a bug in the replacement, to be fixed with a corpus case.
