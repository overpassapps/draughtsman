/**
 * The four sample diagrams an empty instance offers (PR-13; design/atlas/help-about-status.md): one flowchart, one
 * architecture, one sequence and one ER. Offered, never forced - nothing seeds a fresh database and nothing creates them
 * unless someone presses the button. They are the repository's own examples, bundled at build time, so they are
 * documents the model and both renderers already handle (the example suites pin them); each lands as an ordinary
 * document with a fresh id, titled "Sample: ...". The flowchart is `sample-order-approval.json`, hand-placed with every
 * connector on an explicit port so the routes are straight and square (QA S7); `decision-flow.json` is a test fixture
 * for the outline-attachment pins, not a sample.
 */
import orderApproval from '../../../docs/examples/sample-order-approval.json';
import checkoutSequence from '../../../docs/examples/checkout-sequence.json';
import orderService from '../../../docs/examples/order-service.json';
import shopSchema from '../../../docs/examples/shop-schema.json';
import { createDocumentFrom } from '../model/documentClient.ts';
import { parseDocument } from '../model/parse.ts';
import type { Document } from '../model/types.ts';

/** In the order the list will show them: flowchart, architecture, sequence, ER. */
export function sampleDocuments(): Document[] {
  return [orderApproval, orderService, checkoutSequence, shopSchema].map((source) => {
    const parsed = parseDocument(source);
    return { ...parsed, title: `Sample: ${parsed.title}` };
  });
}

/** Creates every sample as a new document, one after another so the list keeps their order; resolves the new ids. */
export async function createSamples(fetchImpl: typeof fetch = fetch): Promise<string[]> {
  const ids: string[] = [];
  for (const sample of sampleDocuments()) {
    ids.push(await createDocumentFrom(sample, fetchImpl));
  }
  return ids;
}
