import { describe, expect, it } from 'vitest';
import { ErrorCodes, RpcError, handleLine, type MethodTable } from '../src/rpc.ts';

const warnings: string[] = [];
const log = { warn: (m: string) => void warnings.push(m) };

const methods: MethodTable = {
  echo: (params) => params,
  boom: () => {
    throw new RpcError(ErrorCodes.invalidDocument, 'bad document', { field: 'nodes' });
  },
  crash: () => {
    throw new Error('unexpected');
  },
  later: async (params) => {
    await new Promise((resolve) => setTimeout(resolve, 5));
    return params;
  },
};

describe('JSON-RPC framing', () => {
  it('answers a request with its id and result', async () => {
    const response = await handleLine('{"jsonrpc":"2.0","id":7,"method":"echo","params":{"a":1}}', methods, log);
    expect(response).toEqual({ jsonrpc: '2.0', id: 7, result: { a: 1 } });
  });

  it('accepts string ids and async handlers', async () => {
    const response = await handleLine('{"jsonrpc":"2.0","id":"x","method":"later","params":[1,2]}', methods, log);
    expect(response).toEqual({ jsonrpc: '2.0', id: 'x', result: [1, 2] });
  });

  it('ignores blank lines and notifications', async () => {
    expect(await handleLine('   ', methods, log)).toBeNull();
    expect(await handleLine('{"jsonrpc":"2.0","method":"echo","params":1}', methods, log)).toBeNull();
    expect(await handleLine('{"jsonrpc":"2.0","method":"missing"}', methods, log)).toBeNull();
    expect(warnings).toContain('notification for unknown method missing ignored');
  });

  it('reports parse errors with a null id', async () => {
    const response = await handleLine('{not json', methods, log);
    expect(response?.id).toBeNull();
    expect(response?.error?.code).toBe(ErrorCodes.parseError);
  });

  it('rejects requests that are not JSON-RPC 2.0 objects', async () => {
    expect((await handleLine('[1,2]', methods, log))?.error?.code).toBe(ErrorCodes.invalidRequest);
    expect((await handleLine('42', methods, log))?.error?.code).toBe(ErrorCodes.invalidRequest);
    expect((await handleLine('{"id":1,"method":"echo"}', methods, log))?.error?.code).toBe(ErrorCodes.invalidRequest);
    expect((await handleLine('{"jsonrpc":"2.0","id":1,"method":5}', methods, log))?.error?.code).toBe(ErrorCodes.invalidRequest);
  });

  it('reports unknown methods', async () => {
    const response = await handleLine('{"jsonrpc":"2.0","id":1,"method":"nope"}', methods, log);
    expect(response?.error).toEqual({ code: ErrorCodes.methodNotFound, message: 'Method not found: nope' });
  });

  it('reports a method named after an inherited object member as unknown', async () => {
    for (const method of ['toString', 'constructor', '__defineGetter__']) {
      const response = await handleLine(`{"jsonrpc":"2.0","id":1,"method":"${method}","params":{}}`, methods, log);
      expect(response?.error).toEqual({ code: ErrorCodes.methodNotFound, message: `Method not found: ${method}` });
    }
  });

  it('maps RpcError to its code and data, other errors to internalError', async () => {
    const rpc = await handleLine('{"jsonrpc":"2.0","id":1,"method":"boom"}', methods, log);
    expect(rpc?.error).toEqual({ code: ErrorCodes.invalidDocument, message: 'bad document', data: { field: 'nodes' } });

    const internal = await handleLine('{"jsonrpc":"2.0","id":2,"method":"crash"}', methods, log);
    expect(internal?.error).toEqual({ code: ErrorCodes.internalError, message: 'unexpected' });
  });
});
