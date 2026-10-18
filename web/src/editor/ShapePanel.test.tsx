// @vitest-environment jsdom
import { fireEvent, render, screen } from '@testing-library/react';
import { diagramThemes } from '../generated/diagram-themes.ts';
import type { NodeTemplate } from '../model/create.ts';
import { ShapePanel } from './ShapePanel.tsx';

describe('ShapePanel', () => {
  it.each([
    ['Document', 'io', 'document'],
    ['Predefined process', 'subprocess', 'predefined-process'],
    ['Manual input', 'io', 'manual-input'],
    ['Off-page connector', 'process', 'off-page-connector'],
  ])('places %s with its explicit presentation on click and drag', (label, kind, shape) => {
    const onPlace = vi.fn();
    const onDragStart = vi.fn();
    render(<ShapePanel documentType="flowchart" theme={diagramThemes.overpass} onPlace={onPlace} onDragStart={onDragStart} onPlaceImage={() => undefined} onClose={() => undefined} />);
    const glyph = screen.getByRole('button', { name: label });
    fireEvent.click(glyph);
    expect(onPlace).toHaveBeenCalledExactlyOnceWith({ kind, shape, label });
    fireEvent.pointerDown(glyph, { pointerId: 3, button: 0, pointerType: 'mouse' });
    expect(onDragStart).toHaveBeenCalledExactlyOnceWith({ kind, shape, label }, 3);
  });

  it("offers the flowchart vocabulary's kinds, not architecture's", () => {
    render(<ShapePanel documentType="flowchart" theme={diagramThemes.overpass} onPlace={() => undefined} onDragStart={() => undefined} onPlaceImage={() => undefined} onClose={() => undefined} />);
    for (const label of ['Process', 'Decision', 'Terminator', 'Data / IO', 'Predefined process', 'Note', 'Container']) {
      expect(screen.getByRole('button', { name: label })).toBeTruthy();
    }
    expect(screen.queryByRole('button', { name: 'Service' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'External system' })).toBeNull();
  });

  it('offers no kind group for freeform, only the presentation shapes', () => {
    render(<ShapePanel documentType="freeform" theme={diagramThemes.overpass} onPlace={() => undefined} onDragStart={() => undefined} onPlaceImage={() => undefined} onClose={() => undefined} />);
    expect(screen.queryByRole('button', { name: 'Process' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Service' })).toBeNull();
    expect(screen.getByRole('button', { name: 'Rectangle' })).toBeTruthy();
  });

  it('calls onPlace with the glyph’s own template on click, and the same template plus the pointer id on pointerdown', () => {
    const placed: NodeTemplate[] = [];
    const dragged: [NodeTemplate, number][] = [];
    render(<ShapePanel documentType="architecture" theme={diagramThemes.overpass} onPlace={(t) => placed.push(t)} onDragStart={(t, pointerId) => dragged.push([t, pointerId])} onPlaceImage={() => undefined} onClose={() => undefined} />);
    fireEvent.click(screen.getByRole('button', { name: 'Database' }));
    expect(placed).toEqual([{ kind: 'database', shape: 'cylinder', label: 'Database' }]);

    fireEvent.pointerDown(screen.getByRole('button', { name: 'Database' }), { pointerId: 7, button: 0, pointerType: 'mouse' });
    expect(dragged).toEqual([[{ kind: 'database', shape: 'cylinder', label: 'Database' }, 7]]);
  });

  it('ignores a non-primary mouse button press, so a right-click never arms a drag', () => {
    const onDragStart = vi.fn();
    render(<ShapePanel documentType="architecture" theme={diagramThemes.overpass} onPlace={() => undefined} onDragStart={onDragStart} onPlaceImage={() => undefined} onClose={() => undefined} />);
    fireEvent.pointerDown(screen.getByRole('button', { name: 'Database' }), { pointerId: 1, button: 2, pointerType: 'mouse' });
    expect(onDragStart).not.toHaveBeenCalled();
  });

  it('opens the image picker for the Image glyph instead of placing or dragging a template', () => {
    const onPlace = vi.fn();
    const onDragStart = vi.fn();
    const onPlaceImage = vi.fn();
    render(<ShapePanel documentType="architecture" theme={diagramThemes.overpass} onPlace={onPlace} onDragStart={onDragStart} onPlaceImage={onPlaceImage} onClose={() => undefined} />);
    const glyph = screen.getByRole('button', { name: 'Image' });
    fireEvent.pointerDown(glyph, { pointerId: 9, button: 0, pointerType: 'mouse' });
    expect(onDragStart).not.toHaveBeenCalled();
    fireEvent.click(glyph);
    expect(onPlaceImage).toHaveBeenCalledTimes(1);
    expect(onPlace).not.toHaveBeenCalled();
  });

  it('collapses and re-expands a group on its header, and closes on Escape', () => {
    const onClose = vi.fn();
    const { container } = render(<ShapePanel documentType="architecture" theme={diagramThemes.overpass} onPlace={() => undefined} onDragStart={() => undefined} onPlaceImage={() => undefined} onClose={onClose} />);
    const header = screen.getByRole('button', { name: /architecture kinds/i });
    expect(header.getAttribute('aria-expanded')).toBe('true');
    expect(screen.getByRole('button', { name: 'Database' })).toBeTruthy();

    fireEvent.click(header);
    expect(header.getAttribute('aria-expanded')).toBe('false');
    expect(screen.queryByRole('button', { name: 'Database' })).toBeNull();

    fireEvent.click(header);
    expect(header.getAttribute('aria-expanded')).toBe('true');

    fireEvent.keyDown(container.querySelector('.shape-panel') ?? container, { key: 'Escape' });
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it('keeps the wider flowchart shapes in a group that starts closed, and places kind and generic symbols alike', () => {
    const onPlace = vi.fn();
    render(<ShapePanel documentType="flowchart" theme={diagramThemes.overpass} onPlace={onPlace} onDragStart={() => undefined} onPlaceImage={() => undefined} onClose={() => undefined} />);
    const header = screen.getByRole('button', { name: 'More flowchart shapes' });
    expect(header.getAttribute('aria-expanded')).toBe('false');
    expect(screen.queryByRole('button', { name: 'Delay' })).toBeNull();

    fireEvent.click(header);
    expect(header.getAttribute('aria-expanded')).toBe('true');

    fireEvent.click(screen.getByRole('button', { name: 'Delay' }));
    expect(onPlace).toHaveBeenCalledWith({ kind: 'delay', shape: 'delay', label: 'Delay' });

    fireEvent.click(screen.getByRole('button', { name: 'Collate' }));
    expect(onPlace).toHaveBeenCalledWith({ kind: 'shape', shape: 'collate', label: 'Collate' });
  });

  it('offers no wider flowchart group for architecture or freeform', () => {
    render(<ShapePanel documentType="architecture" theme={diagramThemes.overpass} onPlace={() => undefined} onDragStart={() => undefined} onPlaceImage={() => undefined} onClose={() => undefined} />);
    expect(screen.queryByRole('button', { name: 'More flowchart shapes' })).toBeNull();
  });

  it('offers the Icons group for architecture, closed by default, placing each icon with its own kind', () => {
    const onPlace = vi.fn();
    render(<ShapePanel documentType="architecture" theme={diagramThemes.overpass} onPlace={onPlace} onDragStart={() => undefined} onPlaceImage={() => undefined} onClose={() => undefined} />);
    const header = screen.getByRole('button', { name: 'Icons' });
    expect(header.getAttribute('aria-expanded')).toBe('false');
    expect(screen.queryByRole('button', { name: 'Server' })).toBeNull();

    fireEvent.click(header);
    expect(header.getAttribute('aria-expanded')).toBe('true');

    // A presentation-only icon places a real vocabulary kind (never the unknown 'shape', which lint flags); one with
    // its own architecture kind uses that kind.
    fireEvent.click(screen.getByRole('button', { name: 'Server' }));
    expect(onPlace).toHaveBeenCalledWith({ kind: 'service', shape: 'custom:generic/server', label: 'Server', size: { w: 120, h: 96 } });
    fireEvent.click(screen.getByRole('button', { name: 'Load balancer' }));
    expect(onPlace).toHaveBeenCalledWith({ kind: 'load-balancer', shape: 'custom:generic/load-balancer', label: 'Load balancer', size: { w: 120, h: 96 } });
    // A VPC is a container boundary, not a glyph over a label: it keeps the everyday box.
    fireEvent.click(screen.getByRole('button', { name: 'VPC' }));
    expect(onPlace).toHaveBeenLastCalledWith({ kind: 'container', shape: 'custom:generic/vpc', label: 'VPC' });
  });

  it('filters the Icons group by name through the shared search, even while it is collapsed', () => {
    render(<ShapePanel documentType="architecture" theme={diagramThemes.overpass} onPlace={() => undefined} onDragStart={() => undefined} onPlaceImage={() => undefined} onClose={() => undefined} />);
    fireEvent.change(screen.getByRole('searchbox', { name: 'Search shapes' }), { target: { value: 'load bal' } });
    expect(screen.getByRole('button', { name: 'Load balancer' })).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Server' })).toBeNull();
  });

  it('offers no Icons group for freeform or flowchart', () => {
    for (const documentType of ['freeform', 'flowchart'] as const) {
      const { unmount } = render(<ShapePanel documentType={documentType} theme={diagramThemes.overpass} onPlace={() => undefined} onDragStart={() => undefined} onPlaceImage={() => undefined} onClose={() => undefined} />);
      expect(screen.queryByRole('button', { name: 'Icons' })).toBeNull();
      unmount();
    }
  });

  it('offers the State group for flowchart and freeform, closed by default, placing each shape with its own kind', () => {
    for (const documentType of ['flowchart', 'freeform'] as const) {
      const onPlace = vi.fn();
      const { unmount } = render(<ShapePanel documentType={documentType} theme={diagramThemes.overpass} onPlace={onPlace} onDragStart={() => undefined} onPlaceImage={() => undefined} onClose={() => undefined} />);
      const header = screen.getByRole('button', { name: 'State' });
      expect(header.getAttribute('aria-expanded'), documentType).toBe('false');
      expect(screen.queryByRole('button', { name: 'Initial' })).toBeNull();

      fireEvent.click(header);
      expect(header.getAttribute('aria-expanded')).toBe('true');

      fireEvent.click(screen.getByRole('button', { name: 'Initial' }));
      expect(onPlace).toHaveBeenCalledWith({ kind: 'initial', shape: 'custom:state/initial', label: 'Initial' });
      fireEvent.click(screen.getByRole('button', { name: 'Composite state' }));
      expect(onPlace).toHaveBeenCalledWith({ kind: 'composite-state', shape: 'custom:state/composite-state', label: 'Composite state' });
      unmount();
    }
  });

  it('offers the Basic shapes group for every type but sequence, closed by default, placing each shape on a kind the type knows', () => {
    const cases = [
      ['flowchart', 'process'],
      ['architecture', 'service'],
      ['freeform', 'shape'],
    ] as const;
    for (const [documentType, kind] of cases) {
      const onPlace = vi.fn();
      const { unmount } = render(<ShapePanel documentType={documentType} theme={diagramThemes.overpass} onPlace={onPlace} onDragStart={() => undefined} onPlaceImage={() => undefined} onClose={() => undefined} />);
      const header = screen.getByRole('button', { name: 'Basic shapes' });
      expect(header.getAttribute('aria-expanded'), documentType).toBe('false');
      expect(screen.queryByRole('button', { name: 'Triangle' })).toBeNull();

      fireEvent.click(header);
      fireEvent.click(screen.getByRole('button', { name: 'Triangle' }));
      expect(onPlace).toHaveBeenCalledWith({ kind, shape: 'custom:basic/triangle', label: 'Triangle', size: { w: 120, h: 100 } });
      // An annotation goes on the note kind, whatever the type.
      fireEvent.click(screen.getByRole('button', { name: 'Callout' }));
      expect(onPlace).toHaveBeenCalledWith({ kind: 'note', shape: 'custom:basic/callout', label: 'Callout', size: { w: 160, h: 90 } });
      unmount();
    }
  });

  it('offers no Basic shapes group for a sequence diagram, which has nowhere to draw one', () => {
    render(<ShapePanel documentType="sequence" theme={diagramThemes.overpass} onPlace={() => undefined} onDragStart={() => undefined} onPlaceImage={() => undefined} onClose={() => undefined} />);
    expect(screen.queryByRole('button', { name: 'Basic shapes' })).toBeNull();
  });

  it('finds a basic shape by another name people use for it, even in a closed group', () => {
    render(<ShapePanel documentType="architecture" theme={diagramThemes.overpass} onPlace={() => undefined} onDragStart={() => undefined} onPlaceImage={() => undefined} onClose={() => undefined} />);
    fireEvent.change(screen.getByRole('searchbox', { name: 'Search shapes' }), { target: { value: 'package' } });
    expect(screen.getByRole('button', { name: 'Folder' })).toBeTruthy();
    fireEvent.change(screen.getByRole('searchbox', { name: 'Search shapes' }), { target: { value: 'use case' } });
    expect(screen.getByRole('button', { name: 'Ellipse' })).toBeTruthy();
  });

  it('offers no State group for architecture', () => {
    render(<ShapePanel documentType="architecture" theme={diagramThemes.overpass} onPlace={() => undefined} onDragStart={() => undefined} onPlaceImage={() => undefined} onClose={() => undefined} />);
    expect(screen.queryByRole('button', { name: 'State' })).toBeNull();
  });

  it('offers the Swimlane group for flowchart and freeform, closed by default, placing pool and lane', () => {
    for (const documentType of ['flowchart', 'freeform'] as const) {
      const onPlace = vi.fn();
      const { unmount } = render(<ShapePanel documentType={documentType} theme={diagramThemes.overpass} onPlace={onPlace} onDragStart={() => undefined} onPlaceImage={() => undefined} onClose={() => undefined} />);
      const header = screen.getByRole('button', { name: 'Swimlane' });
      expect(header.getAttribute('aria-expanded'), documentType).toBe('false');
      expect(screen.queryByRole('button', { name: 'Pool' })).toBeNull();

      fireEvent.click(header);
      expect(header.getAttribute('aria-expanded')).toBe('true');

      fireEvent.click(screen.getByRole('button', { name: 'Pool' }));
      expect(onPlace).toHaveBeenCalledWith({ kind: 'pool', shape: 'custom:swimlane/pool', label: 'Pool', size: { w: 640, h: 320 } });
      fireEvent.click(screen.getByRole('button', { name: 'Lane' }));
      expect(onPlace).toHaveBeenCalledWith({ kind: 'lane', shape: 'custom:swimlane/lane', label: 'Lane' });
      unmount();
    }
  });

  it('offers no Swimlane group for architecture', () => {
    render(<ShapePanel documentType="architecture" theme={diagramThemes.overpass} onPlace={() => undefined} onDragStart={() => undefined} onPlaceImage={() => undefined} onClose={() => undefined} />);
    expect(screen.queryByRole('button', { name: 'Swimlane' })).toBeNull();
  });
  it('offers the BPMN group for flowchart and freeform, closed by default, placing each shape with its own kind and size', () => {
    for (const documentType of ['flowchart', 'freeform'] as const) {
      const onPlace = vi.fn();
      const { unmount } = render(<ShapePanel documentType={documentType} theme={diagramThemes.overpass} onPlace={onPlace} onDragStart={() => undefined} onPlaceImage={() => undefined} onClose={() => undefined} />);
      const header = screen.getByRole('button', { name: 'BPMN' });
      expect(header.getAttribute('aria-expanded'), documentType).toBe('false');
      expect(screen.queryByRole('button', { name: 'User task' })).toBeNull();

      fireEvent.click(header);
      expect(header.getAttribute('aria-expanded')).toBe('true');
      expect(screen.getAllByRole('group', { name: 'BPMN' }).length).toBe(1);

      fireEvent.click(screen.getByRole('button', { name: 'User task' }));
      expect(onPlace).toHaveBeenCalledWith({ kind: 'bpmn-user-task', shape: 'custom:bpmn/user-task', label: 'User task', size: { w: 100, h: 70 } });
      fireEvent.click(screen.getByRole('button', { name: 'Message start event' }));
      expect(onPlace).toHaveBeenCalledWith({ kind: 'bpmn-start-message', shape: 'custom:bpmn/start-message', label: 'Message start event', size: { w: 40, h: 40 } });
      unmount();
    }
  });

  it('offers no BPMN group for architecture, and finds one shape by name without every BPMN shape matching', () => {
    const { unmount } = render(<ShapePanel documentType="architecture" theme={diagramThemes.overpass} onPlace={() => undefined} onDragStart={() => undefined} onPlaceImage={() => undefined} onClose={() => undefined} />);
    expect(screen.queryByRole('button', { name: 'BPMN' })).toBeNull();
    unmount();

    render(<ShapePanel documentType="flowchart" theme={diagramThemes.overpass} onPlace={() => undefined} onDragStart={() => undefined} onPlaceImage={() => undefined} onClose={() => undefined} />);
    fireEvent.change(screen.getByRole('searchbox', { name: 'Search shapes' }), { target: { value: 'gateway' } });
    // Searching opens every group with a match; only the five gateways carry the word.
    expect(screen.getAllByRole('button').filter((button) => /gateway/i.test(button.getAttribute('aria-label') ?? '')).length).toBe(5);
    fireEvent.change(screen.getByRole('searchbox', { name: 'Search shapes' }), { target: { value: 'process' } });
    // "Process" must not drag in the 35 BPMN shapes: they match on their own names, not on a shared keyword.
    expect(screen.queryByRole('button', { name: 'Message start event' })).toBeNull();
  });

  it('offers the UML class group for architecture and freeform, closed by default, placing each shape sized and populated', () => {
    for (const documentType of ['architecture', 'freeform'] as const) {
      const onPlace = vi.fn();
      const { unmount } = render(<ShapePanel documentType={documentType} theme={diagramThemes.overpass} onPlace={onPlace} onDragStart={() => undefined} onPlaceImage={() => undefined} onClose={() => undefined} />);
      const header = screen.getByRole('button', { name: 'UML class' });
      expect(header.getAttribute('aria-expanded'), documentType).toBe('false');
      expect(screen.queryByRole('button', { name: 'Class' })).toBeNull();

      fireEvent.click(header);
      expect(header.getAttribute('aria-expanded')).toBe('true');

      fireEvent.click(screen.getByRole('button', { name: 'Class' }));
      expect(onPlace).toHaveBeenLastCalledWith({
        kind: 'class',
        shape: 'custom:uml/class',
        label: 'ClassName',
        size: { w: 156, h: 84 },
        props: { attributes: ['- attribute: Type'], operations: ['+ operation(): void'] },
      });
      fireEvent.click(screen.getByRole('button', { name: 'Provided interface' }));
      expect(onPlace).toHaveBeenLastCalledWith({ kind: 'provided-interface', shape: 'custom:uml/lollipop', label: 'IName', size: { w: 72, h: 48 } });
      for (const name of ['Interface', 'Required interface']) {
        expect(screen.getByRole('button', { name })).toBeTruthy();
      }
      unmount();
    }
  });

  it('offers the ER entity group for architecture and freeform, closed by default, placing each shape sized and populated', () => {
    for (const documentType of ['architecture', 'freeform'] as const) {
      const onPlace = vi.fn();
      const { unmount } = render(<ShapePanel documentType={documentType} theme={diagramThemes.overpass} onPlace={onPlace} onDragStart={() => undefined} onPlaceImage={() => undefined} onClose={() => undefined} />);
      const header = screen.getByRole('button', { name: 'ER entity' });
      expect(header.getAttribute('aria-expanded'), documentType).toBe('false');
      expect(screen.queryByRole('button', { name: 'Entity' })).toBeNull();

      fireEvent.click(header);
      expect(header.getAttribute('aria-expanded')).toBe('true');

      fireEvent.click(screen.getByRole('button', { name: 'Entity' }));
      expect(onPlace).toHaveBeenLastCalledWith({
        kind: 'entity',
        shape: 'custom:er/entity',
        label: 'TableName',
        size: { w: 270, h: 92 },
        props: { columns: ['PK Id int not null', 'Name nvarchar(100) not null', 'Notes nvarchar(max) null'] },
      });
      fireEvent.click(screen.getByRole('button', { name: 'Weak entity' }));
      expect(onPlace).toHaveBeenLastCalledWith(expect.objectContaining({ kind: 'weak-entity', shape: 'custom:er/weak-entity', size: { w: 310, h: 92 } }));
      unmount();
    }
  });

  it('offers no ER entity group for flowchart, and finds the entities by a table or database search', () => {
    const { unmount } = render(<ShapePanel documentType="flowchart" theme={diagramThemes.overpass} onPlace={() => undefined} onDragStart={() => undefined} onPlaceImage={() => undefined} onClose={() => undefined} />);
    expect(screen.queryByRole('button', { name: 'ER entity' })).toBeNull();
    unmount();

    render(<ShapePanel documentType="freeform" theme={diagramThemes.overpass} onPlace={() => undefined} onDragStart={() => undefined} onPlaceImage={() => undefined} onClose={() => undefined} />);
    fireEvent.change(screen.getByRole('searchbox'), { target: { value: 'table' } });
    expect(screen.getByRole('button', { name: 'Weak entity' })).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Class' })).toBeNull();
  });

  it('offers no UML class group for flowchart', () => {
    render(<ShapePanel documentType="flowchart" theme={diagramThemes.overpass} onPlace={() => undefined} onDragStart={() => undefined} onPlaceImage={() => undefined} onClose={() => undefined} />);
    expect(screen.queryByRole('button', { name: 'UML class' })).toBeNull();
  });

  it('finds the UML shapes by search even while their group is closed', () => {
    render(<ShapePanel documentType="freeform" theme={diagramThemes.overpass} onPlace={() => undefined} onDragStart={() => undefined} onPlaceImage={() => undefined} onClose={() => undefined} />);
    fireEvent.change(screen.getByRole('searchbox'), { target: { value: 'lollipop' } });
    expect(screen.getByRole('button', { name: 'Provided interface' })).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Required interface' })).toBeNull();
  });

  it('names every glyph with a visible caption, not only a hover title (QA-05)', () => {
    const { container } = render(<ShapePanel documentType="architecture" theme={diagramThemes.overpass} onPlace={() => undefined} onDragStart={() => undefined} onPlaceImage={() => undefined} onClose={() => undefined} />);
    const glyphs = Array.from(container.querySelectorAll<HTMLElement>('.shape-panel-glyph'));
    expect(glyphs.length).toBeGreaterThan(10);
    for (const glyph of glyphs) {
      const caption = glyph.querySelector('.shape-panel-glyph-name');
      // The caption is the visible name; the accessible name stays the button's own aria-label, never read twice.
      expect(caption?.textContent, glyph.outerHTML).toBe(glyph.getAttribute('aria-label'));
      expect((caption?.textContent ?? '').trim()).not.toBe('');
      expect(caption?.getAttribute('aria-hidden')).toBe('true');
    }
    expect(Array.from(glyphs).some((glyph) => glyph.textContent === 'Service')).toBe(true);
  });
});
