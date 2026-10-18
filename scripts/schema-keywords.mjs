#!/usr/bin/env node
// Lists every JSON Schema keyword, `type`, `format` and `$ref` target that docs/schema/document.schema.json uses, and
// how often. It is the evidence behind docs/adr/0004-document-schema-validator.md: the in-house validator in
// src/Draughtsman.Core/Serialization/SchemaValidator.cs must implement exactly this set, and
// `SchemaValidatorTests.The_committed_schema_uses_only_keywords_the_validator_implements` pins that from the C# side.
//
// Usage: node scripts/schema-keywords.mjs [path/to/schema.json]
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const file = process.argv[2] ?? join(here, '..', 'docs', 'schema', 'document.schema.json');
const schema = JSON.parse(readFileSync(file, 'utf8'));

// Keywords whose value is a schema, a map of schemas or an array of schemas; everything else is a leaf value.
const SCHEMA_VALUED = new Set(['additionalProperties', 'items', 'not', 'if', 'then', 'else', 'contains', 'propertyNames',
  'unevaluatedProperties', 'unevaluatedItems', 'additionalItems']);
const SCHEMA_MAP = new Set(['properties', '$defs', 'definitions', 'patternProperties', 'dependentSchemas']);
const SCHEMA_ARRAY = new Set(['allOf', 'anyOf', 'oneOf', 'prefixItems']);

const keywords = new Map();
const types = new Map();
const formats = new Map();
const refs = new Map();
const enums = [];
const consts = [];
const patterns = new Set();
let subschemas = 0;

const bump = (map, key) => map.set(key, (map.get(key) ?? 0) + 1);

function walk(node, pointer) {
  if (node === true || node === false) { bump(keywords, `(boolean schema ${node})`); return; }
  subschemas++;
  for (const [key, value] of Object.entries(node)) {
    bump(keywords, key);
    if (key === 'type') (Array.isArray(value) ? value : [value]).forEach(t => bump(types, t));
    if (key === 'format') bump(formats, value);
    if (key === '$ref') bump(refs, value);
    if (key === 'enum') enums.push({ at: pointer, values: value });
    if (key === 'const') consts.push({ at: pointer, value });
    if (key === 'pattern') patterns.add(value);
    if (SCHEMA_VALUED.has(key) && typeof value === 'object') walk(value, `${pointer}/${key}`);
    else if (SCHEMA_MAP.has(key)) for (const [name, sub] of Object.entries(value)) walk(sub, `${pointer}/${key}/${name}`);
    else if (SCHEMA_ARRAY.has(key)) value.forEach((sub, i) => walk(sub, `${pointer}/${key}/${i}`));
  }
}
walk(schema, '#');

const table = (title, map) => {
  console.log(`\n${title}`);
  for (const [k, v] of [...map].sort((a, b) => b[1] - a[1] || a[0].localeCompare(b[0]))) console.log(`  ${String(v).padStart(4)}  ${k}`);
};
console.log(`${file}\nsubschemas: ${subschemas}`);
table('keywords', keywords);
table('type values', types);
table('format values', formats);
table('$ref targets', refs);
console.log(`\nenum: ${enums.length} (${enums.map(e => e.values.length).join(', ')} values)`);
console.log(`const: ${consts.map(c => `${c.at} = ${JSON.stringify(c.value)}`).join('; ')}`);
console.log(`patterns: ${patterns.size}`);
for (const p of patterns) console.log(`  ${p.length > 100 ? `${p.slice(0, 97)}...` : p}`);
