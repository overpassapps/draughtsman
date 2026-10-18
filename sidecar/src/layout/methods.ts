/**
 * The layout.elk JSON-RPC method: validates params, runs the layout, returns { document, warnings }.
 * Options live in `options` ({ algorithm, direction }), the shape the .NET client sends; values are the
 * lower-case strings the README lists.
 */
import { ErrorCodes, RpcError, isObject } from '../rpc.ts';
import { assertDocumentShape } from '../yjs/shape.ts';
import { layoutDocument, type Algorithm, type Direction, type LayoutOptions, type LayoutResult } from './elk.ts';

const ALGORITHMS: readonly Algorithm[] = ['layered', 'stress', 'tree'];

const DIRECTIONS: readonly Direction[] = ['down', 'right', 'up', 'left'];

export async function layoutElk(params: unknown): Promise<LayoutResult> {
  if (!isObject(params)) {
    throw new RpcError(ErrorCodes.invalidParams, 'params must be an object');
  }
  const document = params['document'];
  assertDocumentShape(document, 'document');
  const options = readOptions(isObject(params['options']) ? params['options'] : {});
  if (document.type === 'sequence') {
    // A sequence diagram's geometry is derived from its edge and node order (sequence.scene), never stored, so there is
    // nothing for ELK to place: hand the document back unchanged. The nodes deliberately carry no geometry.
    return { document: document as unknown as LayoutResult['document'], warnings: [] };
  }
  return layoutDocument(document, options);
}

function readOptions(raw: Record<string, unknown>): LayoutOptions {
  const options: LayoutOptions = {};
  const algorithm = raw['algorithm'];
  if (algorithm !== undefined && algorithm !== null) {
    if (typeof algorithm !== 'string' || !ALGORITHMS.includes(algorithm as Algorithm)) {
      throw new RpcError(ErrorCodes.invalidParams, `algorithm must be one of ${ALGORITHMS.join(', ')}`);
    }
    options.algorithm = algorithm as Algorithm;
  }
  const direction = raw['direction'];
  if (direction !== undefined && direction !== null) {
    if (typeof direction !== 'string' || !DIRECTIONS.includes(direction as Direction)) {
      throw new RpcError(ErrorCodes.invalidParams, `direction must be one of ${DIRECTIONS.join(', ')}`);
    }
    options.direction = direction as Direction;
  }
  return options;
}
