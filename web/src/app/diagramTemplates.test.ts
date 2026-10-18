import architecture from '../../../src/Draughtsman.Core/Schemas/architecture.json';
import flowchart from '../../../src/Draughtsman.Core/Schemas/flowchart.json';
import { parseDocument } from '../model/parse.ts';
import { DIAGRAM_TEMPLATES } from './diagramTemplates.ts';

const kinds = { architecture, flowchart } as unknown as Record<string, { nodeKinds: { name: string }[]; edgeKinds: { name: string }[] }>;
const meta = { createdAt: '2026-10-01T00:00:00.000Z', updatedAt: '2026-10-01T00:00:00.000Z', createdBy: 't' };

describe('More diagram types (P5)', () => {
  it('offers ER, UML class, BPMN, swimlane and org chart, each made as an existing document type', () => {
    expect(DIAGRAM_TEMPLATES.map((template) => template.id)).toEqual(['er', 'uml-class', 'bpmn', 'swimlane', 'org-chart']);
    expect(new Set(DIAGRAM_TEMPLATES.map((template) => template.type))).toEqual(new Set(['architecture', 'flowchart']));
  });

  it.each(DIAGRAM_TEMPLATES.map((template) => [template.id, template] as const))('%s is a valid document using only the type\'s own kinds, with every node placed', (_id, template) => {
    const document = parseDocument({ id: 'doc_t', schemaVersion: 1, title: template.title, type: template.type, nodes: template.nodes(), edges: template.edges(), meta });
    const vocabulary = kinds[template.type];
    expect(document.nodes.length).toBeGreaterThan(1);
    for (const node of document.nodes) {
      expect(vocabulary?.nodeKinds.map((kind) => kind.name), `${template.id}: ${node.kind}`).toContain(node.kind);
      expect(node.geometry, `${template.id}: ${node.id} has a position`).toBeDefined();
    }
    for (const edge of document.edges) {
      expect(vocabulary?.edgeKinds.map((kind) => kind.name), `${template.id}: ${edge.kind}`).toContain(edge.kind);
    }
  });

  it('starts the swimlane diagram as a pool with its lanes inside it', () => {
    const template = DIAGRAM_TEMPLATES.find((candidate) => candidate.id === 'swimlane');
    const nodes = template?.nodes() ?? [];
    expect(nodes.map((node) => node.kind)).toEqual(['pool', 'lane', 'lane']);
    expect(nodes.slice(1).every((lane) => lane.parent === nodes[0]?.id)).toBe(true);
  });
});
