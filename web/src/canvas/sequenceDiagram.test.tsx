// @vitest-environment jsdom
import { render } from '@testing-library/react';
import { documentToSvg } from '../editor/exportDiagram.tsx';
import { diagramThemes } from '../generated/diagram-themes.ts';
import { parseDocument } from '../model/parse.ts';
import { checkoutFragments, checkoutSequence, must } from '../model/sequenceFixture.ts';
import { ACTOR_SHAPE, sequenceScene } from '../model/sequenceScene.ts';
import { nodeKindDefault } from '../theme/resolveStyle.ts';
import { renderShape } from './shapes.tsx';
import type { Document } from '../model/types.ts';
import { DiagramSvg } from './DiagramSvg.tsx';
import { indexDocument } from './documentIndex.ts';
import { routingSnapshot } from './edgeRoutes.ts';

const theme = diagramThemes.overpass;
const viewport = { x: 0, y: 0, zoom: 1 };
const size = { width: 900, height: 600 };

function draw(document: Document, extra: Partial<Parameters<typeof DiagramSvg>[0]> = {}) {
  return render(<DiagramSvg document={document} theme={theme} viewport={viewport} size={size} {...extra} />);
}

describe('indexDocument for a sequence document', () => {
  it('fills placed, nodeBounds and edgePaths from the derived scene, ignoring stored geometry and warning about none', () => {
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => undefined);
    try {
      const document = checkoutSequence();
      // A stored position is never read for this type; a node with none draws where the scene puts it.
      document.nodes[2] = { ...must(document.nodes[2]), geometry: { x: 900, y: 900, w: 10, h: 10 } };
      const index = indexDocument(document);
      expect(index.placed.get('p_web')?.geometry).toEqual({ x: 160, y: 20, w: 120, h: 40 });
      expect(index.placed.get('p_api')?.geometry).toEqual({ x: 300, y: 20, w: 120, h: 40 });
      expect(index.nodeBounds.get('p_pay')).toEqual({ x: 440, y: 20, w: 120, h: 40 });
      expect(index.edgePaths.get('m_2')?.points).toEqual([{ x: 220, y: 144 }, { x: 355, y: 144 }]);
      expect(index.edgePaths.get('m_7')?.points).toHaveLength(4);
      expect(index.contentBounds).toEqual({ x: 0, y: 0, w: 580, h: 378 });
      expect(index.unplaced.size).toBe(0);
      expect(index.rootNodes.map((node) => node.id)).toEqual(['p_user', 'p_web', 'p_api', 'p_pay']);
      expect(index.sequence).toEqual(sequenceScene(document));
      expect(warn).not.toHaveBeenCalled();
    } finally {
      warn.mockRestore();
    }
  });

  it('gives every other document type no scene', () => {
    expect(indexDocument({ ...checkoutSequence(), type: 'freeform', nodes: [], edges: [] }).sequence).toBeUndefined();
  });

  it('never sends a message to the router, whatever routing it names', () => {
    const document = checkoutSequence();
    document.edges[0] = { ...must(document.edges[0]), routing: { mode: 'orthogonal' } };
    expect(routingSnapshot(indexDocument(document)).inputs.size).toBe(0);
  });

  it('widens a self message’s box to hold a label plate wider than its bracket', () => {
    const document = checkoutSequence();
    document.edges[6] = { ...must(document.edges[6]), label: 'a self message label far wider than its little bracket' };
    const index = indexDocument(document);
    const box = must(index.edgeBounds.get('m_7'));
    const message = must(must(index.sequence).messages[6]);
    const plateRight = must(message.labelAt).x + must(message.labelWidth) / 2 + 4;
    expect(plateRight).toBeGreaterThan(must(must(index.edgePaths.get('m_7')).points[1]).x + 24);
    expect(box.x + box.w).toBeGreaterThanOrEqual(plateRight);
  });
});

describe('DiagramSvg for a sequence document', () => {
  it('draws a header per participant, a message per edge, a lifeline per participant and the activation bars', () => {
    const { container } = draw(checkoutSequence());
    expect([...container.querySelectorAll('g.nodes > g[data-id]')].map((g) => g.getAttribute('data-id'))).toEqual(['p_user', 'p_web', 'p_api', 'p_pay']);
    expect([...container.querySelectorAll('g.edges > g[data-id]')].map((g) => g.getAttribute('data-id'))).toEqual(['m_1', 'm_2', 'm_3', 'm_4', 'm_5', 'm_6', 'm_7']);
    const lifelines = [...container.querySelectorAll('line.lifeline')];
    expect(lifelines.map((line) => [line.getAttribute('data-lifeline'), line.getAttribute('x1'), line.getAttribute('y1'), line.getAttribute('y2')])).toEqual([
      ['p_user', '80', '60', '378'],
      ['p_web', '220', '60', '378'],
      ['p_api', '360', '60', '378'],
      ['p_pay', '500', '60', '378'],
    ]);
    expect(lifelines[0]?.getAttribute('stroke-dasharray')).toBe('6 4');
    const bars = [...container.querySelectorAll('rect.activation')];
    expect(bars.map((bar) => ['data-lifeline', 'x', 'y', 'width', 'height'].map((name) => bar.getAttribute(name)))).toEqual([
      ['p_api', '355', '144', '10', '144'],
      ['p_pay', '495', '180', '10', '36'],
    ]);
  });

  it('draws the lifelines and bars beneath the nodes and edges, and inert to the pointer', () => {
    const { container } = draw(checkoutSequence());
    const world = must(container.querySelector('g.world'));
    expect([...world.children].map((child) => child.getAttribute('class'))).toEqual(['sequence-layer', 'nodes', 'edges']);
    expect(container.querySelector('g.sequence-layer')?.getAttribute('style')).toContain('pointer-events: none');
  });

  it('styles a message by its kind: return dashed with an open head, sync solid with a filled one', () => {
    const { container } = draw(checkoutSequence());
    const line = (id: string) => must(container.querySelector(`g.edges g[data-id="${id}"] polyline:not(.hit-area)`));
    expect(line('m_1').getAttribute('stroke-dasharray')).toBeNull();
    expect(line('m_4').getAttribute('stroke-dasharray')).toBe('6 4');
    expect(line('m_5').getAttribute('stroke-dasharray')).toBeNull();
    expect(line('m_1').getAttribute('marker-end')).toMatch(/arrow-arrow-/);
    expect(line('m_4').getAttribute('marker-end')).toMatch(/arrow-open-/);
    expect(line('m_5').getAttribute('marker-end')).toMatch(/arrow-open-/);
  });

  it('draws each label above its own line, not on it', () => {
    const { container } = draw(checkoutSequence());
    const label = (id: string) => must(container.querySelector(`g.edges g[data-id="${id}"] g.edge-label text`));
    expect(label('m_1').textContent).toBe('Place order');
    expect([label('m_1').getAttribute('x'), label('m_1').getAttribute('y')]).toEqual(['150', '94']);
    expect([label('m_2').getAttribute('x'), label('m_2').getAttribute('y')]).toEqual(['287.5', '130']);
  });

  it('draws a self message as a bracket', () => {
    const { container } = draw(checkoutSequence());
    expect(container.querySelector('g.edges g[data-id="m_7"] polyline:not(.hit-area)')?.getAttribute('points')).toBe('360,326 390,326 390,342 360,342');
  });

  it('follows the participant’s style down its lifeline and bars', () => {
    const document = checkoutSequence();
    document.nodes[2] = { ...must(document.nodes[2]), style: { stroke: '#123456', fill: '#654321' } };
    const { container } = draw(document);
    expect(container.querySelector('line[data-lifeline="p_api"]')?.getAttribute('stroke')).toBe('#123456');
    expect(container.querySelector('rect[data-lifeline="p_api"]')?.getAttribute('fill')).toBe('#654321');
    expect(container.querySelector('line[data-lifeline="p_web"]')?.getAttribute('stroke')).not.toBe('#123456');
  });

  it('draws a document with no messages as headers and lifelines', () => {
    const { container } = draw({ ...checkoutSequence(), edges: [] });
    expect(container.querySelectorAll('g.edges > g')).toHaveLength(0);
    expect(container.querySelectorAll('line.lifeline')).toHaveLength(4);
    expect(container.querySelectorAll('rect.activation')).toHaveLength(0);
  });
});

describe('export of a sequence document', () => {
  it('crops to the scene’s bounds and carries the lifelines, bars, messages and labels', () => {
    const { svg, width, height } = documentToSvg(checkoutSequence(), theme);
    expect(width).toBe(580 + 48);
    expect(height).toBe(378 + 48);
    expect(svg).toContain('data-lifeline="p_api"');
    expect(svg).toContain('class="activation"');
    for (const text of ['Customer', 'Web app', 'Orders API', 'Payments', 'Place order', 'POST /orders', 'charge(total)', 'receipt', 'declined', '201 Created', 'audit()']) {
      expect(svg).toContain(text);
    }
    // The picture is the export surface's: no editor grid or selection chrome.
    expect(svg).not.toContain('canvas-grid');
    expect(svg).toContain('data-surface="export"');
  });

  it('exports what the schema-validated document parses to, so a document read from JSON draws the same', () => {
    const document = checkoutSequence();
    const parsed = parseDocument(JSON.parse(JSON.stringify(document)));
    expect(documentToSvg(parsed, theme).svg).toBe(documentToSvg(document, theme).svg);
  });
});

describe('a sequence document with a fragment, a note and a created participant', () => {
  it('indexes the frame as a placed node that is not a root, and the note as an edge with a box outline', () => {
    const index = indexDocument(checkoutFragments());
    expect(index.placed.get('f_1')?.geometry).toEqual({ x: 200, y: 198, w: 315, h: 160 });
    expect(index.nodeBounds.get('f_1')).toEqual({ x: 200, y: 198, w: 315, h: 160 });
    // The node renderer draws only the participants; the sequence layer draws the frame.
    expect(index.rootNodes.map((node) => node.id)).toEqual(['p_user', 'p_web', 'p_api', 'p_pay', 'p_mail']);
    expect(index.edgePaths.get('n_1')?.points).toEqual([{ x: 210, y: 166 }, { x: 370, y: 166 }, { x: 370, y: 194 }, { x: 210, y: 194 }, { x: 210, y: 166 }]);
    expect(index.contentBounds).toEqual({ x: 0, y: 0, w: 720, h: 544 });
    // The created participant's header is at its create row, not at the top.
    expect(index.placed.get('p_mail')?.geometry).toEqual({ x: 580, y: 408, w: 120, h: 40 });
  });

  it('draws the frame, tab, divider and guards, the note box, and the X, each selectable by its own data-id', () => {
    const { container } = draw(checkoutFragments());
    const frame = must(container.querySelector('g.node[data-id="f_1"]'));
    expect(frame.getAttribute('data-kind')).toBe('fragment');
    expect(['x', 'y', 'width', 'height'].map((name) => frame.querySelector('rect.frame')?.getAttribute(name))).toEqual(['200', '198', '315', '160']);
    expect(frame.querySelector('line.divider')?.getAttribute('y1')).toBe('292');
    expect(frame.querySelector('path.tag')?.getAttribute('d')).toBe('M 200 198 H 231 V 210 L 225 216 H 200 Z');
    expect(frame.querySelector('text.operator')?.textContent).toBe('alt');
    expect([...frame.querySelectorAll('text.guard')].map((text) => [text.textContent, text.getAttribute('x'), text.getAttribute('y')])).toEqual([['[card ok]', '239', '207'], ['[declined]', '208', '301']]);

    const note = must(container.querySelector('g.edge[data-id="n_1"]'));
    expect(note.querySelector('path.note-box')?.getAttribute('d')).toBe('M 210 166 H 362 L 370 174 V 194 H 210 Z');
    expect(note.textContent).toBe('Idempotent by key');
    // A note is a box, not also a line: the edge loop never draws it.
    expect(container.querySelectorAll('g.edges > g[data-id="n_1"]')).toHaveLength(0);

    expect(container.querySelector('path.destroy')?.getAttribute('d')).toBe('M 632 498 L 648 514 M 648 498 L 632 514');
    expect(container.querySelector('line[data-lifeline="p_mail"]')?.getAttribute('y1')).toBe('448');
    expect(container.querySelector('line[data-lifeline="p_mail"]')?.getAttribute('y2')).toBe('506');
  });

  it('draws no fragment as a header box and keeps the frame’s inside clickable through to the messages', () => {
    const { container } = draw(checkoutFragments());
    // The frame’s hit target is its border only (a stroke), so a message inside it still gets the press.
    const hit = must(container.querySelector('g.node[data-id="f_1"] rect.hit-area'));
    expect(hit.getAttribute('fill')).toBe('none');
    expect(hit.getAttribute('style')).toContain('pointer-events: stroke');
    expect(container.querySelectorAll('g.nodes > g[data-id="f_1"]')).toHaveLength(0);
    // Every message's own hit stroke is still there.
    expect(container.querySelectorAll('g.edges > g[data-id="m_3"] polyline.hit-area')).toHaveLength(1);
  });

  it('leaves the frame and note unselectable on the export surface and out of the editing text while one is edited', () => {
    const exported = documentToSvg(checkoutFragments(), theme).svg;
    expect(exported).not.toContain('class="hit-area"');
    for (const text of ['alt', '[card ok]', '[declined]', 'Idempotent by key', 'new Mailer()', 'close()']) {
      expect(exported).toContain(text);
    }
    // While a label is being edited in place, the box's own text is left out so the editor is the only one drawn.
    const { container } = draw(checkoutFragments(), { editingLabel: 'n_1' });
    expect(must(container.querySelector('g.edge[data-id="n_1"]')).textContent).toBe('');
    // A guard being edited is left out of the frame and the frame's other text stays.
    const editingFrame = draw(checkoutFragments(), { editingGuard: { fragment: 'f_1', operand: 1 } }).container;
    expect([...editingFrame.querySelectorAll('g.node[data-id="f_1"] text')].map((text) => text.textContent)).toEqual(['alt', '[card ok]']);
  });
});

describe('an actor participant', () => {
  // The checkout's Customer is stored as `actor` with the kind's default shape `rounded`; it draws as a UML stick figure
  // over the lifeline in the top 26 of its 120 by 40 header: a figure 22 high (that band less 2 either side), head radius 3.52,
  // the name in the 14 under it.
  it('draws a stick figure centred over its lifeline and no box, its name under the figure, and leaves other participants boxes', () => {
    const { container } = draw(checkoutSequence());
    const actor = must(container.querySelector('g.node[data-id="p_user"]'));
    expect(actor.querySelectorAll('rect.shape[fill="transparent"]')).toHaveLength(1);
    const head = must(actor.querySelector('circle.shape-detail'));
    expect([head.getAttribute('cx'), head.getAttribute('cy'), head.getAttribute('r')]).toEqual(['60', '5.52', '3.52']);
    // Body, arms and legs in one path: neck (60, 9.04) down to the hip (60, 16.3: 2 + 0.65 of 22), arms at 11.68 reaching 7.92, feet 6.16 out at 24.
    expect(actor.querySelector('path.shape-detail')?.getAttribute('d')).toBe('M60,9.04 V16.3 M52.08,11.68 H67.92 M53.84,24 L60,16.3 L66.16,24');
    // The name is laid out in the band under the figure: 26 to 40, the header's full width.
    expect(renderShape(ACTOR_SHAPE, 120, 40, nodeKindDefault(theme, 'default')).labelBox).toEqual({ x: 0, y: 26, w: 120, h: 14 });
    expect(must(container.querySelector('g.node[data-id="p_web"]')).querySelector('circle')).toBeNull();
  });

  it('draws the stick figure for the actor shape named outright and for a stored rounded one, but not for another kind', () => {
    const document = checkoutSequence();
    document.nodes[1] = { ...must(document.nodes[1]), shape: 'rounded' };
    expect(indexDocument(document).placed.get('p_user')?.shape).toBe('custom:sequence/actor');
    expect(indexDocument(document).placed.get('p_web')?.shape).toBe('rounded');
    document.nodes[0] = { ...must(document.nodes[0]), shape: 'custom:sequence/actor' };
    expect(indexDocument(document).placed.get('p_user')?.shape).toBe('custom:sequence/actor');
    document.nodes[0] = { ...must(document.nodes[0]), shape: 'pill' };
    expect(indexDocument(document).placed.get('p_user')?.shape).toBe('pill');
  });
});

describe('the colours of an exported sequence document', () => {
  // The shipped overpass diagram theme on the export surface. SvgRenderer's sequence rendering resolves the same tokens from the
  // same YAML in C#, and McpHttpToolsTests pins these very literals for the server's picture, so the two exports cannot drift.
  const exported = new DOMParser().parseFromString(documentToSvg(checkoutFragments(), theme).svg, 'image/svg+xml');
  const attributes = (selector: string, ...names: string[]): (string | null)[] => names.map((name) => must(exported.querySelector(selector)).getAttribute(name));

  it('paints a participant, its lifeline and its activation bar from the participant kind', () => {
    expect(attributes('g[data-id="p_web"] rect.shape', 'fill', 'stroke')).toEqual(['#202a42', '#7f8aa0']);
    expect(attributes('line[data-lifeline="p_web"]', 'stroke')).toEqual(['#7f8aa0']);
    expect(attributes('rect[data-lifeline="p_api"]', 'fill', 'stroke')).toEqual(['#202a42', '#7f8aa0']);
    expect(exported.querySelector('g[data-id="p_web"] text.label')?.getAttribute('fill')).toBe('#F2F2F2');
  });

  it('paints an actor’s figure from the actor kind and a message from its edge kind', () => {
    expect(attributes('g[data-id="p_user"] circle.shape-detail', 'fill', 'stroke')).toEqual(['#151e33', '#7f8aa0']);
    expect(attributes('g[data-id="m_1"] polyline', 'stroke', 'stroke-width')).toEqual(['#7f8aa0', '1.5']);
    expect(attributes('g[data-id="m_4"] polyline, g[data-id="m_4"] line', 'stroke-dasharray')).toEqual(['6 4']);
  });

  it('draws text on the paper, and a see-through frame’s text, in the surface’s colour over a white plate', () => {
    expect(attributes('g[data-id="m_1"] .edge-label rect', 'fill')).toEqual(['#FFFFFF']);
    expect(attributes('g[data-id="m_1"] .edge-label text', 'fill')).toEqual(['#111111']);
    expect(attributes('g[data-id="f_1"] rect.frame', 'stroke')).toEqual(['#7f8aa0']);
    expect(attributes('g[data-id="f_1"] path.tag', 'fill', 'stroke')).toEqual(['#FFFFFF', '#7f8aa0']);
    expect(attributes('g[data-id="f_1"] text.guard', 'fill')).toEqual(['#111111']);
    expect(attributes('g[data-id="n_1"] path.note-box', 'fill', 'stroke')).toEqual(['#FFFFFF', '#7f8aa0']);
    expect(attributes('g[data-id="n_1"] text', 'fill')).toEqual(['#111111']);
  });
});

describe('an interaction use', () => {
  const withUse = (): Document => {
    const document = checkoutSequence();
    document.edges.splice(1, 0, { id: 'u_1', kind: 'interaction-use', label: 'Authenticate', from: { node: 'p_web' }, to: { node: 'p_api' } });
    return document;
  };

  it('draws a ref frame over the lifelines it covers, with the cut-corner tab and its name, selectable by its own data-id', () => {
    const { container } = draw(withUse());
    const frame = must(container.querySelector('g.edge[data-id="u_1"]'));
    expect(frame.getAttribute('data-kind')).toBe('interaction-use');
    // The row is 126-186 after the first message (90-126), so the frame is 132-180, across p_web (220) to p_api (360) plus 10 each side.
    expect(['x', 'y', 'width', 'height'].map((name) => frame.querySelector('rect.note-box')?.getAttribute(name))).toEqual(['210', '132', '160', '48']);
    expect(frame.querySelector('path.tag')?.getAttribute('d')).toBe('M 210 132 H 241 V 144 L 235 150 H 210 Z');
    expect(frame.querySelector('text.operator')?.textContent).toBe('ref');
    expect(frame.textContent).toBe('refAuthenticate');
    // It is a box, not also a line, and it is filled so the lifelines do not run through it.
    expect(container.querySelectorAll('g.edges > g[data-id="u_1"]')).toHaveLength(0);
    expect(frame.querySelector('rect.note-box')?.getAttribute('fill')).not.toBe('none');
    expect(indexDocument(withUse()).edgePaths.get('u_1')?.points).toHaveLength(5);
  });

  it('exports with the frame, the tab and the name, and leaves the name out while it is edited in place', () => {
    const exported = documentToSvg(withUse(), theme).svg;
    expect(exported).toContain('Authenticate');
    expect(exported).toContain('class="tag"');
    expect(must(draw(withUse(), { editingLabel: 'u_1' }).container.querySelector('g.edge[data-id="u_1"]')).textContent).toBe('ref');
  });
});
