/**
 * Spawns the real bundle (dist/sidecar.js, built by `pretest`) and drives it over stdio the way
 * Draughtsman.Server does. Catches bundling and framing mistakes the unit tests cannot.
 */
import { spawn, type ChildProcessByStdio } from 'node:child_process';
import { existsSync } from 'node:fs';
import { createInterface } from 'node:readline';
import { join } from 'node:path';
import type { Readable, Writable } from 'node:stream';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import type { LaidOutDocument } from '../src/model/types.ts';
import type { JsonRpcResponse } from '../src/rpc.ts';
import { assertWellFormedLayout, exampleWithoutGeometry, loadExample, repoRoot } from './helpers.ts';

const bundle = join(repoRoot, 'sidecar', 'dist', 'sidecar.js');

class SidecarHarness {
  private readonly process: ChildProcessByStdio<Writable, Readable, Readable>;
  private readonly pending = new Map<number, (response: JsonRpcResponse) => void>();
  private nextId = 1;
  readonly stderr: string[] = [];

  constructor() {
    this.process = spawn(process.execPath, [bundle], { stdio: ['pipe', 'pipe', 'pipe'] });
    createInterface({ input: this.process.stdout }).on('line', (line) => {
      const response = JSON.parse(line) as JsonRpcResponse;
      const resolve = this.pending.get(response.id as number);
      this.pending.delete(response.id as number);
      resolve?.(response);
    });
    createInterface({ input: this.process.stderr }).on('line', (line) => this.stderr.push(line));
  }

  call(method: string, params?: unknown): Promise<JsonRpcResponse> {
    const id = this.nextId++;
    return new Promise((resolve) => {
      this.pending.set(id, resolve);
      this.process.stdin.write(`${JSON.stringify({ jsonrpc: '2.0', id, method, params })}\n`);
    });
  }

  raw(line: string): void {
    this.process.stdin.write(`${line}\n`);
  }

  close(): Promise<number | null> {
    return new Promise((resolve) => {
      this.process.on('exit', (code) => resolve(code));
      this.process.stdin.end();
    });
  }
}

describe('dist/sidecar.js over stdio', () => {
  let sidecar: SidecarHarness;

  beforeAll(() => {
    if (!existsSync(bundle)) {
      throw new Error(`${bundle} is missing; run npm run build first`);
    }
    sidecar = new SidecarHarness();
  });

  afterAll(async () => {
    const code = await sidecar.close();
    expect(code).toBe(0);
  });

  it('answers ping with its version', async () => {
    const response = await sidecar.call('ping');
    expect(response.result).toMatchObject({ ok: true, version: expect.stringMatching(/^\d+\.\d+\.\d+/), node: process.version });
  });

  it('lays out the example and round-trips it through Yjs, answering interleaved requests', async () => {
    const [layout, fromJson, health] = await Promise.all([
      sidecar.call('layout.elk', { document: exampleWithoutGeometry(), options: { algorithm: 'layered' } }),
      sidecar.call('yjs.fromJson', { document: loadExample() }),
      sidecar.call('ping'),
    ]);

    expect(layout.error).toBeUndefined();
    const laidOut = (layout.result as { document: LaidOutDocument; warnings: string[] });
    expect(laidOut.warnings).toEqual([]);
    assertWellFormedLayout(laidOut.document.nodes);

    const state = (fromJson.result as { state: string }).state;
    expect(state.length).toBeGreaterThan(0);
    const toJson = await sidecar.call('yjs.toJson', { state });
    expect(toJson.result).toEqual({ document: loadExample() });
    expect(health.result).toMatchObject({ ok: true });
  });

  it('routes orthogonal edges through the bundled editor router', async () => {
    const document = loadExample();
    document.edges = document.edges.map((edge) => ({ ...edge, routing: { mode: 'orthogonal' } }));
    const response = await sidecar.call('route.document', { document });
    expect(response.error).toBeUndefined();
    const { routes } = response.result as { routes: Record<string, { points: { x: number; y: number }[] }> };
    expect(Object.keys(routes).sort()).toEqual(document.edges.map((edge) => edge.id).sort());
    for (const route of Object.values(routes)) {
      expect(route.points.every((p, i) => i === 0 || p.x === route.points[i - 1]!.x || p.y === route.points[i - 1]!.y)).toBe(true);
    }
  });

  it('keeps running after a bad frame and reports it', async () => {
    sidecar.raw('this is not json');
    const response = await sidecar.call('ping');
    expect(response.result).toMatchObject({ ok: true });
    const bad = await sidecar.call('layout.elk', { document: { nodes: [], edges: 'x' } });
    expect(bad.error?.code).toBe(-32000);
  });

  it('writes nothing but frames to stdout', () => {
    // Every stdout line so far parsed as JSON inside the harness; stray output would have thrown there.
    expect(sidecar.stderr.some((line) => line.includes('sidecar: '))).toBe(false);
  });
});
