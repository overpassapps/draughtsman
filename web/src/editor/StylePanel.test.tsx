// @vitest-environment jsdom
import { fireEvent, render, screen } from '@testing-library/react';
import { diagramThemes } from '../generated/diagram-themes.ts';
import type { Edge, Node } from '../model/types.ts';
import { StylePanel } from './StylePanel.tsx';
import { swatchName } from './styleState.ts';

const node = (id: string, style?: Node['style']): Node => ({
  id,
  kind: 'service',
  label: 'A',
  shape: 'rectangle',
  ...(style === undefined ? {} : { style }),
});

const edge = (id: string, style?: Edge['style']): Edge => ({
  id,
  kind: 'flow',
  from: { node: 'a' },
  to: { node: 'b' },
  ...(style === undefined ? {} : { style }),
});

describe('StylePanel', () => {
  it('shows the shape fields for a node selection and the connector fields for an edge', () => {
    const { rerender } = render(
      <StylePanel target="node" nodes={[node('n_a')]} edges={[]} theme={diagramThemes.overpass} onStyle={() => undefined} onMouseDown={() => undefined} />,
    );
    // Shape-only controls: fill swatches and a font-size input exist; there is no dash-only distinction needed.
    expect(screen.getByRole('group', { name: 'Fill colour' })).toBeTruthy();
    expect(screen.getByLabelText('Font size')).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Bold' })).toBeTruthy();

    rerender(
      <StylePanel target="edge" nodes={[]} edges={[edge('e_1')]} theme={diagramThemes.overpass} onStyle={() => undefined} onMouseDown={() => undefined} />,
    );
    expect(screen.queryByRole('group', { name: 'Fill colour' })).toBeNull();
    expect(screen.getByRole('group', { name: 'Stroke colour' })).toBeTruthy();
    expect(screen.getByLabelText('Stroke width')).toBeTruthy();
  });

  it('fires onStyle with the chosen palette colour for the node target', () => {
    const onStyle = vi.fn();
    render(<StylePanel target="node" nodes={[node('n_a')]} edges={[]} theme={diagramThemes.overpass} onStyle={onStyle} onMouseDown={() => undefined} />);
    // The first swatch in the overpass palette is red.
    fireEvent.click(screen.getByRole('button', { name: 'Fill Red' }));
    expect(onStyle).toHaveBeenCalledWith('fill', diagramThemes.overpass.palette.red, 'node');
  });

  it('marks a shared stroke width as pressed and resets a field to undefined', () => {
    const onStyle = vi.fn();
    render(
      <StylePanel
        target="node"
        nodes={[node('n_a', { strokeWidth: 2 }), node('n_b', { strokeWidth: 2 })]}
        edges={[]}
        theme={diagramThemes.overpass}
        onStyle={onStyle}
        onMouseDown={() => undefined}
      />,
    );
    fireEvent.click(screen.getByRole('button', { name: 'Reset stroke width' }));
    expect(onStyle).toHaveBeenCalledWith('strokeWidth', undefined, 'node');
  });

  it('shows a mixed value and replaces it when the user types a shared hex', () => {
    const onStyle = vi.fn();
    render(
      <StylePanel
        target="node"
        nodes={[node('n_a', { fill: '#111111' }), node('n_b', { fill: '#222222' })]}
        edges={[]}
        theme={diagramThemes.overpass}
        onStyle={onStyle}
        onMouseDown={() => undefined}
      />,
    );
    const input = screen.getByLabelText<HTMLInputElement>('Fill hex value');
    expect(input.placeholder).toBe('Mixed');
    fireEvent.change(input, { target: { value: '#333333' } });
    fireEvent.blur(input);
    expect(onStyle).toHaveBeenCalledWith('fill', '#333333', 'node');
  });
});
describe('StylePanel names and shows its values (P7)', () => {
  it('names every swatch as a person would, with its hex in the tooltip', () => {
    expect(swatchName('redDeep')).toBe('Red deep');
    expect(swatchName('charcoal')).toBe('Charcoal');
    render(<StylePanel target="node" nodes={[node('n_a')]} edges={[]} theme={diagramThemes.overpass} onStyle={() => undefined} onMouseDown={() => undefined} />);
    const deep = screen.getByRole('button', { name: 'Fill Red deep' });
    expect(deep.getAttribute('title')).toBe(`Red deep (${(diagramThemes.overpass.palette.redDeep ?? '').toLowerCase()})`);
    expect(screen.queryByRole('button', { name: /redDeep/ })).toBeNull();
  });

  it('writes the swatch\'s name beside the hex of the colour in use, and Custom for one off the palette', () => {
    const { rerender } = render(<StylePanel target="node" nodes={[node('n_a', { fill: diagramThemes.overpass.palette.amber ?? '#e0a526' })]} edges={[]} theme={diagramThemes.overpass} onStyle={() => undefined} onMouseDown={() => undefined} />);
    expect(screen.getByRole('group', { name: 'Fill colour' }).closest('.panel-style-row')?.querySelector('.panel-style-colour-name')?.textContent).toBe('Amber');
    rerender(<StylePanel target="node" nodes={[node('n_a', { fill: '#123456' })]} edges={[]} theme={diagramThemes.overpass} onStyle={() => undefined} onMouseDown={() => undefined} />);
    expect(screen.getByRole('group', { name: 'Fill colour' }).closest('.panel-style-row')?.querySelector('.panel-style-colour-name')?.textContent).toBe('Custom');
  });

  it('shows the font size a shape is drawn at, not an empty box, until it is overridden', () => {
    const { rerender } = render(<StylePanel target="node" nodes={[node('n_a')]} edges={[]} theme={diagramThemes.overpass} onStyle={() => undefined} onMouseDown={() => undefined} />);
    expect(screen.getByLabelText<HTMLInputElement>('Font size').value).toBe('14');
    rerender(<StylePanel target="node" nodes={[node('n_a', { fontSize: 20 })]} edges={[]} theme={diagramThemes.overpass} onStyle={() => undefined} onMouseDown={() => undefined} />);
    expect(screen.getByLabelText<HTMLInputElement>('Font size').value).toBe('20');
  });
});
