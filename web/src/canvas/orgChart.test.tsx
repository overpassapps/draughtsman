// @vitest-environment jsdom
import { render } from '@testing-library/react';
import { diagramThemes } from '../generated/diagram-themes.ts';
import { ORG_SHAPES, ORG_SHAPE_PREFIX } from '../model/orgShapes.ts';
import type { Document } from '../model/types.ts';
import { nodeKindDefault } from '../theme/resolveStyle.ts';
import { DiagramSvg } from './DiagramSvg.tsx';
import { renderShape } from './shapes.tsx';

const style = nodeKindDefault(diagramThemes.overpass, 'role');
const size = { width: 800, height: 600 };
const viewport = { x: 0, y: 0, zoom: 1 };

/** The markup a shape draws, so an unimplemented one (which falls through to a plain rectangle) can be told apart. */
function markup(shape: string): string {
  const { container, unmount } = render(<svg>{renderShape(shape, 200, 100, style).element}</svg>);
  const html = container.innerHTML;
  unmount();
  return html;
}

function orgDocument(kind: string): Document {
  return {
    id: 'doc_org',
    schemaVersion: 1,
    title: 'Org',
    type: 'architecture',
    nodes: [
      { id: 'n_a', kind: 'role', label: 'Name\nTitle', shape: 'custom:org/role', geometry: { x: 0, y: 0, w: 160, h: 80 } },
      { id: 'n_b', kind: 'role', label: 'Name\nTitle', shape: 'custom:org/role', geometry: { x: 300, y: 0, w: 160, h: 80 } },
    ],
    edges: [{ id: 'e_1', kind, from: { node: 'n_a' }, to: { node: 'n_b' } }],
    meta: { createdAt: '2026-09-29T00:00:00.000Z', updatedAt: '2026-09-29T00:00:00.000Z', createdBy: 'test' },
  };
}

/** The stroke dasharray on the edge's drawn line, or undefined when it draws solid. */
function edgeDash(kind: string): string | null | undefined {
  const { container, unmount } = render(<DiagramSvg document={orgDocument(kind)} theme={diagramThemes.overpass} viewport={viewport} size={size} />);
  const dash = container.querySelector('g[data-id="e_1"] polyline:not(.hit-area)')?.getAttribute('stroke-dasharray');
  unmount();
  return dash;
}

describe('org chart shapes', () => {
  it('draws every org box as an org box, not the fallback rectangle', () => {
    const fallback = markup('rectangle');
    expect(ORG_SHAPES.length).toBeGreaterThan(0);
    for (const box of ORG_SHAPES) {
      const shape = ORG_SHAPE_PREFIX + box.name;
      const html = markup(shape);
      expect(html, `${shape} renders as the fallback rectangle`).not.toBe(fallback);
      expect(html).toContain('shape org');
    }

    // The two-sided case: a name the library does not carry is not an org box and draws as the fallback.
    expect(markup(`${ORG_SHAPE_PREFIX}not-a-real-box`)).toBe(fallback);
  });

  it('gives the executive a double border, the assistant a spine, and draws the vacancy dashed', () => {
    expect(markup('custom:org/executive')).toContain('shape-detail');
    expect(markup('custom:org/assistant')).toContain('<line');
    expect(markup('custom:org/vacancy')).toContain('stroke-dasharray="6 4"');
    // The role box is the plain case: one outline, no detail, no dash.
    const role = markup('custom:org/role');
    expect(role).not.toContain('shape-detail');
    expect(role).not.toContain('stroke-dasharray');
  });

  it('renders the dotted-line report edge dashed and a plain flow edge solid', () => {
    expect(edgeDash('dotted-report')).toBe('6 4');
    // Two-sided: the dash comes from the edge's kind, so an ordinary flow edge draws solid.
    expect(edgeDash('flow')).toBeNull();
  });
});
