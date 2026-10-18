// @vitest-environment jsdom
import { createRef } from 'react';
import { render } from '@testing-library/react';
import { diagramThemes } from '../generated/diagram-themes.ts';
import { parseDocument } from '../model/parse.ts';
import type { Node } from '../model/types.ts';
import { DiagramCanvas, type DiagramCanvasHandle } from './DiagramCanvas.tsx';

const meta = { createdAt: '2026-10-01T00:00:00.000Z', updatedAt: '2026-10-01T00:00:00.000Z', createdBy: 't' };
const box = (id: string, extra: Partial<Node> = {}): Node => ({ id, kind: 'service', label: id, shape: 'rectangle', geometry: { x: 0, y: 0, w: 160, h: 80 }, ...extra });

/** The ids of the member-editing commands the canvas registers for a document holding these nodes. */
function memberCommands(nodes: Node[]): string[] {
  const handle = createRef<DiagramCanvasHandle>();
  render(
    <DiagramCanvas ref={handle} document={parseDocument({ id: 'doc_m', schemaVersion: 1, title: 'M', type: 'architecture', nodes, edges: [], meta })} theme={diagramThemes.overpass} size={{ width: 800, height: 600 }} routingBackend={() => undefined} onLabelChange={() => undefined} onMembersChange={() => undefined} />,
  );
  return (handle.current?.actions ?? []).map((action) => action.id).filter((id) => /^canvas\.edit(Attributes|Operations|Columns)$/.test(id));
}

describe('Edit attributes / operations / columns (P6)', () => {
  it('are not offered in a document with no class or table in it, so a flowchart\'s menu does not list them', () => {
    expect(memberCommands([box('a'), box('b')])).toEqual([]);
  });

  it('offers a class\'s two compartments only when a class is there, and columns only for a table', () => {
    const uml = box('c', { kind: 'class', shape: 'custom:uml/class', props: { attributes: [], operations: [] } });
    const er = box('t', { kind: 'entity', shape: 'custom:er/entity', props: { columns: [] } });
    expect(memberCommands([box('a'), uml])).toEqual(['canvas.editAttributes', 'canvas.editOperations']);
    expect(memberCommands([box('a'), er])).toEqual(['canvas.editColumns']);
    expect(memberCommands([uml, er])).toEqual(['canvas.editAttributes', 'canvas.editOperations', 'canvas.editColumns']);
  });
});
