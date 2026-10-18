import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import type { Document, Geometry, LaidOutNode } from '../src/model/types.ts';

export const repoRoot = join(dirname(fileURLToPath(import.meta.url)), '..', '..');

export const examplePath = join(repoRoot, 'docs', 'examples', 'order-service.json');

/** A fresh copy of docs/examples/order-service.json each time so tests cannot leak mutations into each other. */
export function loadExample(): Document {
  return JSON.parse(readFileSync(examplePath, 'utf8')) as Document;
}

/** The example with every node's geometry removed, as a DSL or Mermaid import would hand it to layout. */
export function exampleWithoutGeometry(): Document {
  const doc = loadExample();
  return {
    ...doc,
    nodes: doc.nodes.map((node) => {
      const { geometry: _geometry, ...rest } = node;
      return rest;
    }),
  };
}

export function overlaps(a: Geometry, b: Geometry): boolean {
  return a.x < b.x + b.w && b.x < a.x + a.w && a.y < b.y + b.h && b.y < a.y + a.h;
}

export function contains(outer: Geometry, inner: Geometry): boolean {
  return inner.x >= outer.x && inner.y >= outer.y && inner.x + inner.w <= outer.x + outer.w && inner.y + inner.h <= outer.y + outer.h;
}

/** Every pair of nodes sharing a parent must not overlap; children must sit inside their container. */
export function assertWellFormedLayout(nodes: LaidOutNode[]): void {
  const byId = new Map(nodes.map((n) => [n.id, n]));
  for (const node of nodes) {
    if (!(node.geometry.w > 0 && node.geometry.h > 0)) {
      throw new Error(`node ${node.id} has no size: ${JSON.stringify(node.geometry)}`);
    }
    if (node.parent !== undefined) {
      const parent = byId.get(node.parent);
      if (parent === undefined || !contains(parent.geometry, node.geometry)) {
        throw new Error(`node ${node.id} ${JSON.stringify(node.geometry)} is outside its container ${JSON.stringify(parent?.geometry)}`);
      }
    }
  }
  for (const a of nodes) {
    for (const b of nodes) {
      if (a.id < b.id && a.parent === b.parent && overlaps(a.geometry, b.geometry)) {
        throw new Error(`nodes ${a.id} and ${b.id} overlap: ${JSON.stringify(a.geometry)} vs ${JSON.stringify(b.geometry)}`);
      }
    }
  }
}
