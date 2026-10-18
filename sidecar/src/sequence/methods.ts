/**
 * The sequence.scene JSON-RPC method: the derived geometry of a sequence diagram, from the editor's own function. Like
 * route.document this bundles the web code rather than copying it: `sequenceScene` (web/src/model/sequenceScene.ts) is
 * what `indexDocument` reads to place the participants and messages on the canvas and in the client export, so the
 * server's render_document and PDF export draw the very picture the editor does. The Dockerfile's sidecar stage copies
 * web/src for the same reason.
 *
 * Params `{ document }` (its `type` must be `sequence`); result `{ scene }`, the plain-data `SequenceScene`: headers,
 * lifelines, messages (polylines and label anchors), activation bars and bounds, all in canvas units. Nothing is read
 * from any stored geometry.
 */
import { sequenceScene, type SequenceScene } from '../../../web/src/model/sequenceScene.ts';
import type { Document as WebDocument } from '../../../web/src/model/types.ts';
import { ErrorCodes, RpcError, isObject } from '../rpc.ts';
import { assertDocumentShape } from '../yjs/shape.ts';

export interface SequenceSceneResult {
  scene: SequenceScene;
}

export function sequenceSceneMethod(params: unknown): SequenceSceneResult {
  if (!isObject(params)) {
    throw new RpcError(ErrorCodes.invalidParams, 'params must be an object');
  }
  const document = params['document'];
  assertDocumentShape(document, 'document');
  if (document.type !== 'sequence') {
    throw new RpcError(ErrorCodes.invalidParams, `document.type must be 'sequence'; got '${String(document.type)}'`);
  }
  return { scene: sequenceScene(document as unknown as WebDocument) };
}
