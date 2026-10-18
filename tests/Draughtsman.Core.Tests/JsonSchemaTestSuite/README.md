# JSON-Schema-Test-Suite (draft 2020-12), the files SchemaValidator is held to

These are unmodified copies of files from the official [JSON-Schema-Test-Suite](https://github.com/json-schema-org/JSON-Schema-Test-Suite)
(MIT licence, `LICENSE` here), commit `5b0ee1613e45fcc2bddac00e07c19cd49b00d8a8` (2026-09-21), under `tests/draft2020-12/`:

`additionalProperties`, `boolean_schema`, `const`, `defs`, `enum`, `exclusiveMaximum`, `exclusiveMinimum`, `items`, `maxItems`, `maxLength`, `maximum`,
`minItems`, `minLength`, `minimum`, `pattern`, `properties`, `ref`, `required`, `type`, and from `optional/`: `bignum`, `float-overflow`, `ecmascript-regex`,
`non-bmp-regex` and `format/date-time`. Those are the keywords `SchemaValidator` implements (src/Draughtsman.Core/Serialization/SchemaValidator.cs;
docs/adr/0004-document-schema-validator.md).

`JsonSchemaTestSuiteTests` runs every group whose schema `SchemaValidator` can compile, and names the groups it skips (a schema that needs `oneOf`, `$anchor`,
`prefixItems`, a list of types, a remote reference, a regular expression the .NET engine does not read as ECMAScript, and so on). The handful of cases where the
validator keeps the behaviour of the schema library it replaced instead of the suite's answer are listed in that test with the reason. Never edit these files:
to update them, replace them with the files of a newer commit and update the commit above.
