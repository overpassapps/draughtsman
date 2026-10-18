import fixture from '../../../docs/examples/editor-session.json';
import architecture from '../../../src/Draughtsman.Core/Schemas/architecture.json';
import flowchart from '../../../src/Draughtsman.Core/Schemas/flowchart.json';
import { editorSessionFixture } from './editorSessionFixture.ts';
import { isDocument } from './parse.ts';

it('reproduces the committed DSL round-trip input through editor commands', () => {
  const session = editorSessionFixture();
  expect(isDocument(session)).toBe(true);
  expect(session).toEqual(fixture);
  for (const { name } of [...architecture.nodeKinds, ...flowchart.nodeKinds]) {
    expect(session.nodes.some((node) => node.kind === name)).toBe(true);
  }
  expect(session.nodes.some((node) => node.id === 'n_deleted')).toBe(false);
  expect(session.nodes.find((node) => node.id === 'n_service')?.geometry?.rotation).toBe(37);
  expect(session.nodes.find((node) => node.id === 'n_service')?.parent).toBe('c_inner');
  expect(session.nodes.find((node) => node.id === 'c_inner')?.parent).toBe('c_outer');
  expect(session.edges.map((edge) => edge.kind).sort()).toEqual(['association', 'data', 'dependency', 'dotted-report', 'flow']);
});
