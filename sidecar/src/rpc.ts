/**
 * JSON-RPC 2.0 over newline-delimited frames, independent of any transport so it can be unit tested.
 * One request per line in, one response per line out; the caller correlates by id and may receive
 * responses out of order. Batches are not supported (the .NET client never sends them).
 */

export type JsonRpcId = string | number | null;

export interface JsonRpcRequest {
  jsonrpc: '2.0';
  id?: JsonRpcId;
  method: string;
  params?: unknown;
}

export interface JsonRpcErrorBody {
  code: number;
  message: string;
  data?: unknown;
}

export interface JsonRpcResponse {
  jsonrpc: '2.0';
  id: JsonRpcId;
  result?: unknown;
  error?: JsonRpcErrorBody;
}

/** Standard JSON-RPC codes plus the sidecar's own range below -32000. */
export const ErrorCodes = {
  parseError: -32700,
  invalidRequest: -32600,
  methodNotFound: -32601,
  invalidParams: -32602,
  internalError: -32603,
  /** The payload is not a Draughtsman document the method can work with. */
  invalidDocument: -32000,
  /** ELK rejected the graph or failed part way through. */
  layoutFailed: -32001,
} as const;

/** Thrown by a method handler to answer with a specific code instead of a generic internal error. */
export class RpcError extends Error {
  readonly code: number;
  readonly data: unknown;

  constructor(code: number, message: string, data?: unknown) {
    super(message);
    this.name = 'RpcError';
    this.code = code;
    this.data = data;
  }
}

export type MethodHandler = (params: unknown) => unknown;

export type MethodTable = Readonly<Record<string, MethodHandler>>;

export interface RpcLogger {
  warn(message: string): void;
}

/**
 * Handles one frame. Returns the response to write, or null for a notification (a request without an id),
 * a blank line, or anything else that JSON-RPC says gets no reply.
 */
export async function handleLine(line: string, methods: MethodTable, log: RpcLogger): Promise<JsonRpcResponse | null> {
  if (line.trim().length === 0) {
    return null;
  }

  let parsed: unknown;
  try {
    parsed = JSON.parse(line);
  } catch (error) {
    return errorResponse(null, ErrorCodes.parseError, 'Parse error', describe(error));
  }

  if (Array.isArray(parsed)) {
    return errorResponse(null, ErrorCodes.invalidRequest, 'Batch requests are not supported');
  }

  if (!isObject(parsed)) {
    return errorResponse(null, ErrorCodes.invalidRequest, 'Request must be a JSON object');
  }

  const id = readId(parsed['id']);
  const isNotification = !('id' in parsed);
  if (parsed['jsonrpc'] !== '2.0') {
    return isNotification ? null : errorResponse(id, ErrorCodes.invalidRequest, 'jsonrpc must be "2.0"');
  }

  const method = parsed['method'];
  if (typeof method !== 'string') {
    return isNotification ? null : errorResponse(id, ErrorCodes.invalidRequest, 'method must be a string');
  }

  const handler = Object.hasOwn(methods, method) ? methods[method] : undefined;
  if (handler === undefined) {
    if (isNotification) {
      log.warn(`notification for unknown method ${method} ignored`);
      return null;
    }
    return errorResponse(id, ErrorCodes.methodNotFound, `Method not found: ${method}`);
  }

  try {
    const result = await handler(parsed['params']);
    return isNotification ? null : { jsonrpc: '2.0', id, result: result ?? null };
  } catch (error) {
    if (isNotification) {
      log.warn(`notification ${method} failed: ${describe(error)}`);
      return null;
    }
    if (error instanceof RpcError) {
      return errorResponse(id, error.code, error.message, error.data);
    }
    return errorResponse(id, ErrorCodes.internalError, describe(error));
  }
}

export function errorResponse(id: JsonRpcId, code: number, message: string, data?: unknown): JsonRpcResponse {
  const error: JsonRpcErrorBody = { code, message };
  if (data !== undefined) {
    error.data = data;
  }
  return { jsonrpc: '2.0', id, error };
}

export function isObject(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function readId(value: unknown): JsonRpcId {
  return typeof value === 'string' || typeof value === 'number' ? value : null;
}

function describe(error: unknown): string {
  if (error instanceof Error) {
    return error.message;
  }
  return String(error);
}
