/**
 * The yjs.* JSON-RPC methods. Binary Yjs payloads cross the boundary as base64 strings.
 */
import * as Y from 'yjs';
import { ErrorCodes, RpcError, isObject } from '../rpc.ts';
import { assertDocumentShape, documentToYDoc, yDocToDocument } from './shape.ts';

export interface YjsFromJsonParams {
  document: unknown;
}

export interface YjsToJsonParams {
  state?: string;
  updates?: string[];
}

export interface YjsApplyUpdatesParams {
  state?: string;
  updates: string[];
}

export interface YjsStateResult {
  state: string;
  stateVector: string;
}

/** yjs.fromJson: document JSON -> full encoded Yjs state. */
export function yjsFromJson(params: unknown): YjsStateResult {
  const p = requireParams<YjsFromJsonParams>(params);
  assertDocumentShape(p.document, 'document');
  const doc = documentToYDoc(p.document);
  return encode(doc);
}

/** yjs.toJson: state and/or an ordered list of updates -> document JSON. */
export function yjsToJson(params: unknown): { document: unknown } {
  const p = requireParams<YjsToJsonParams>(params);
  if (p.state === undefined && (p.updates === undefined || p.updates.length === 0)) {
    throw new RpcError(ErrorCodes.invalidParams, 'yjs.toJson needs state, updates or both');
  }
  const doc = new Y.Doc();
  applyAll(doc, p.state, p.updates);
  return { document: yDocToDocument(doc) };
}

/** yjs.applyUpdates: optional starting state plus updates in order -> the full state after them. */
export function yjsApplyUpdates(params: unknown): YjsStateResult {
  const p = requireParams<YjsApplyUpdatesParams>(params);
  if (!Array.isArray(p.updates)) {
    throw new RpcError(ErrorCodes.invalidParams, 'updates must be an array of base64 strings');
  }
  const doc = new Y.Doc();
  applyAll(doc, p.state, p.updates);
  return encode(doc);
}

function applyAll(doc: Y.Doc, state: string | undefined, updates: string[] | undefined): void {
  if (state !== undefined) {
    apply(doc, decodeBase64(state, 'state'), 'state');
  }
  if (updates !== undefined) {
    if (!Array.isArray(updates)) {
      throw new RpcError(ErrorCodes.invalidParams, 'updates must be an array of base64 strings');
    }
    updates.forEach((update, index) => {
      apply(doc, decodeBase64(update, `updates[${index}]`), `updates[${index}]`);
    });
  }
}

function apply(doc: Y.Doc, update: Uint8Array, what: string): void {
  try {
    Y.applyUpdate(doc, update);
  } catch (error) {
    throw new RpcError(ErrorCodes.invalidParams, `${what} is not a valid Yjs update: ${error instanceof Error ? error.message : String(error)}`);
  }
}

function encode(doc: Y.Doc): YjsStateResult {
  return {
    state: encodeBase64(Y.encodeStateAsUpdate(doc)),
    stateVector: encodeBase64(Y.encodeStateVector(doc)),
  };
}

export function decodeBase64(value: unknown, what: string): Uint8Array {
  if (typeof value !== 'string') {
    throw new RpcError(ErrorCodes.invalidParams, `${what} must be a base64 string`);
  }
  const bytes = Buffer.from(value, 'base64');
  // Buffer.from silently drops invalid characters; reject input that does not round-trip so corruption is loud.
  if (value.length > 0 && bytes.length === 0) {
    throw new RpcError(ErrorCodes.invalidParams, `${what} is not valid base64`);
  }
  return new Uint8Array(bytes.buffer, bytes.byteOffset, bytes.byteLength);
}

export function encodeBase64(bytes: Uint8Array): string {
  return Buffer.from(bytes.buffer, bytes.byteOffset, bytes.byteLength).toString('base64');
}

function requireParams<T>(params: unknown): T {
  if (!isObject(params)) {
    throw new RpcError(ErrorCodes.invalidParams, 'params must be an object');
  }
  return params as T;
}
