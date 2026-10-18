/**
 * Synthetic benchmark document generator.
 *
 * Produces a valid Draughtsman document of exactly 2,000 nodes and 2,500 edges, the scale named by checklist item 29
 * of the M2 editor checklist ("pans and zooms at a sustained 60 fps"). A real client architecture diagram is 20 to
 * 60 nodes, so the only way to exercise that budget is with a committed synthetic document: the model plus this
 * generator are committed together, and the output is written to docs/examples/perf-2000n-2500e.json where the perf
 * test (web/src/canvas/DiagramSvg.perf.test.tsx) reads it through the same schema validation as every other example.
 *
 * Determinism: the document is a pure function of the seed below. Node positions come from a fixed grid, kinds cycle
 * through the architecture vocabulary, and labels come from a mulberry32 PRNG seeded with BENCHMARK_SEED. Re-running
 * the generator always writes byte-identical output, and scripts/generate-benchmark.test.ts asserts that the committed
 * fixture is exactly what this file generates, so a hand edit or an unregenerated generator change fails the build.
 *
 * Runs under Node's type stripping (`node --experimental-strip-types`), so keep to erasable TypeScript: no enums,
 * no parameter properties, no namespaces.
 */

import { mkdirSync, realpathSync, writeFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import type { Document, Node, Edge } from '../src/model/types.ts';

/** Fixed seed: the same seed reproduces the same document on every run and on every machine. */
const BENCHMARK_SEED = 0x5eed5eed;

/** The architecture vocabulary (src/Draughtsman.Core/Schemas/architecture.json); container and image are left out. */
const NODE_KINDS: readonly string[] = ['service', 'database', 'queue', 'actor', 'external', 'note'];

/** defaultShape per kind, matching the architecture vocabulary so a node's shape never disagrees with its kind. */
const NODE_SHAPES: Readonly<Record<string, string>> = {
  service: 'rounded',
  database: 'cylinder',
  queue: 'rectangle',
  actor: 'rounded',
  external: 'rectangle',
  note: 'rectangle',
};

const NODE_COUNT = 2000;
const EDGE_COUNT = 2500;

/** Grid layout: this many columns across, cells sized so every node has room and stays on a whole-pixel position. */
const GRID_COLUMNS = 50;
const CELL_W = 220;
const CELL_H = 120;
const NODE_W = 160;
const NODE_H = 64;

/** Edge kinds from the architecture vocabulary. */
const EDGE_KINDS: readonly string[] = ['flow', 'data', 'dependency', 'association'];

/** mulberry32: a tiny deterministic PRNG keyed by an integer seed. */
function mulberry32(seed: number): () => number {
  let a = seed >>> 0;
  return () => {
    a = (a + 0x6d2b79f5) | 0;
    let t = Math.imul(a ^ (a >>> 15), 1 | a);
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

/** Picks the Nth entry of a non-empty list deterministically from the PRNG. */
function pick<T>(rng: () => number, list: readonly T[]): T {
  const entry = list[Math.floor(rng() * list.length)];
  if (entry === undefined) {
    throw new Error('cannot pick from an empty list');
  }
  return entry;
}

export interface Generated {
  document: Document;
}

/** Builds the document as a pure function of BENCHMARK_SEED. */
export function generate(seed = BENCHMARK_SEED): Generated {
  const rng = mulberry32(seed);

  const nodes: Node[] = [];
  for (let i = 0; i < NODE_COUNT; i += 1) {
    const col = i % GRID_COLUMNS;
    const row = Math.floor(i / GRID_COLUMNS);
    const kind = pick(rng, NODE_KINDS);
    const shape = NODE_SHAPES[kind] ?? 'rectangle';
    nodes.push({
      id: `n_${String(i)}`,
      kind,
      label: `${kind} ${String(i)}`,
      shape,
      geometry: { x: col * CELL_W, y: row * CELL_H, w: NODE_W, h: NODE_H },
    });
  }

  // Connect every node to the cell to its right and the cell below, in document order, then keep exactly EDGE_COUNT.
  // Right-neighbour edges come first (GRID_COLUMNS - 1 per row), then down-neighbour edges, so the first EDGE_COUNT
  // are distinct, never self-referential, and every endpoint has authored geometry. No duplicates because each pair is
  // reached once.
  const edges: Edge[] = [];
  const addEdge = (from: number, to: number, kind: string): void => {
    edges.push({ id: `e_${String(edges.length)}`, kind, from: { node: `n_${String(from)}` }, to: { node: `n_${String(to)}` } });
  };
  for (let i = 0; i < NODE_COUNT && edges.length < EDGE_COUNT; i += 1) {
    const col = i % GRID_COLUMNS;
    if (col < GRID_COLUMNS - 1) {
      addEdge(i, i + 1, pick(rng, EDGE_KINDS));
    }
  }
  for (let i = 0; i < NODE_COUNT && edges.length < EDGE_COUNT; i += 1) {
    const below = i + GRID_COLUMNS;
    if (below < NODE_COUNT) {
      addEdge(i, below, pick(rng, EDGE_KINDS));
    }
  }

  if (edges.length !== EDGE_COUNT) {
    throw new Error(`expected ${String(EDGE_COUNT)} edges, generated ${String(edges.length)}`);
  }

  const document: Document = {
    id: 'doc_perf_benchmark',
    schemaVersion: 1,
    title: 'Perf benchmark (2000 nodes, 2500 edges)',
    type: 'architecture',
    nodes,
    edges,
    meta: {
      createdAt: '2026-09-24T00:00:00.000Z',
      updatedAt: '2026-09-24T00:00:00.000Z',
      createdBy: 'benchmark',
    },
  };

  return { document };
}

/** The committed fixture's path and exact bytes: the generated-artefact contract the drift test holds the file to. */
export const BENCHMARK_PATH = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..', 'docs', 'examples', 'perf-2000n-2500e.json');

export function serialise(document: Document): string {
  return `${JSON.stringify(document, null, 2)}\n`;
}

function main(): void {
  const outPath = BENCHMARK_PATH;

  const { document } = generate();
  if (document.nodes.length !== NODE_COUNT || document.edges.length !== EDGE_COUNT) {
    throw new Error(`generator produced ${String(document.nodes.length)} nodes and ${String(document.edges.length)} edges`);
  }

  mkdirSync(dirname(outPath), { recursive: true });
  writeFileSync(outPath, serialise(document));

  console.log(`generate-benchmark: wrote ${outPath} (${String(document.nodes.length)} nodes, ${String(document.edges.length)} edges)`);
}

if (process.argv[1] !== undefined && realpathSync(process.argv[1]) === fileURLToPath(import.meta.url)) {
  main();
}
