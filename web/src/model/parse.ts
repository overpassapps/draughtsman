import Ajv2020, { type ValidateFunction } from 'ajv/dist/2020';
import addFormats from 'ajv-formats';
import schema from '../../../docs/schema/document.schema.json';
import type { Document } from './types.ts';

let validator: ValidateFunction<Document> | undefined;

function getValidator(): ValidateFunction<Document> {
  if (!validator) {
    const ajv = new Ajv2020({ allErrors: true, strict: true });
    addFormats(ajv);
    validator = ajv.compile<Document>(schema);
  }
  return validator;
}

/** Validates unknown JSON against the shared schema and narrows it to a Document. */
export function isDocument(value: unknown): value is Document {
  return getValidator()(value);
}

/**
 * Parses unknown JSON into a Document, throwing with every schema error listed when it does not conform.
 * The schema is the contract, so this stays in step with the C# side without a second hand-written validator.
 */
export function parseDocument(value: unknown): Document {
  const validate = getValidator();
  if (validate(value)) {
    return value;
  }
  const errors = (validate.errors ?? []).map((e) => `${e.instancePath || '/'} ${e.message ?? ''}`.trim());
  throw new Error(`Not a Draughtsman document:\n${errors.join('\n')}`);
}
