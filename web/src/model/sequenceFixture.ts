/**
 * The checkout sequence used by the scene, index, render and export tests: four participants, six messages and a self
 * message, two activation bars. It is docs/examples/checkout-sequence.json, the file docs/sequence-diagrams.md prints as
 * DSL and the C# tests read too, so every side is testing the same document.
 */

import checkout from '../../../docs/examples/checkout-sequence.json';
import checkoutFragmentsJson from '../../../docs/examples/checkout-fragments.json';
import { parseDocument } from './parse.ts';
import type { Document } from './types.ts';

/** A fresh copy of docs/examples/checkout-sequence.json each call, so a test can change it freely. */
export function checkoutSequence(): Document {
  return structuredClone(parseDocument(checkout));
}

/**
 * docs/examples/checkout-fragments.json: the checkout with an alt fragment, a note, and a created and destroyed participant
 * (milestone 2). The C# tests read the same file, and the server-render test asserts the same numbers the scene test pins.
 */
export function checkoutFragments(): Document {
  return structuredClone(parseDocument(checkoutFragmentsJson));
}

/** The value, which the test has already arranged to exist; throws a readable error rather than an `undefined` access. */
export function must<T>(value: T | undefined | null): T {
  if (value === undefined || value === null) {
    throw new Error('expected a value, got none');
  }
  return value;
}
