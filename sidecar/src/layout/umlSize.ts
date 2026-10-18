/**
 * The size a UML class shape needs when a node arrives without one (a DSL or agent-written class has no geometry): a
 * class or interface as tall as its name rows and members need, a lollipop or socket its fixed size. This mirrors
 * `layoutClass` in web/src/model/umlShapes.ts and `UmlShapeLibrary.LayoutClass` in Draughtsman.Core; the metrics are
 * copied here because the sidecar image is built from sidecar/ alone, and test/umlSize.test.ts fails if they drift
 * from src/Draughtsman.Core/Schemas/uml-shapes.json.
 */
import type { Node } from '../model/types.ts';

export const UML_SHAPE_PREFIX = 'custom:uml/';

export const UML_METRICS = { rowHeight: 18, padX: 8, padY: 5, charWidth: 7.5, glyphColumn: 12, minWidth: 120 } as const;

/** The fixed size of the lollipop and socket symbols. */
export const UML_SYMBOL_SIZE = { w: 72, h: 48 } as const;

/** The stereotype an interface shows by default, before any `props.stereotype`. */
const DEFAULT_STEREOTYPE: Readonly<Record<string, string>> = { interface: 'interface' };

const VISIBILITY = '+-#~';

function members(props: Node['props'], key: string): { visibility: string; text: string }[] {
  const list = props?.[key];
  if (!Array.isArray(list)) {
    return [];
  }
  return list
    .filter((item): item is string => typeof item === 'string' && item.trim().length > 0)
    .map((line) => {
      const trimmed = line.trim();
      const first = trimmed.charAt(0);
      return first !== '' && VISIBILITY.includes(first) ? { visibility: first, text: trimmed.slice(1).trimStart() } : { visibility: '', text: trimmed };
    });
}

/** The natural size of a `custom:uml/<name>` node, or undefined when the node is not a UML shape. */
export function umlSizeOf(node: Node): { w: number; h: number } | undefined {
  if (!node.shape.startsWith(UML_SHAPE_PREFIX)) {
    return undefined;
  }
  const name = node.shape.slice(UML_SHAPE_PREFIX.length);
  if (name === 'lollipop' || name === 'socket') {
    return UML_SYMBOL_SIZE;
  }
  if (name !== 'class' && name !== 'interface') {
    return undefined;
  }
  const m = UML_METRICS;
  const custom = node.props?.['stereotype'];
  const stereotype = (typeof custom === 'string' ? custom.trim() : (DEFAULT_STEREOTYPE[name] ?? '')) || undefined;
  const attributes = members(node.props, 'attributes');
  const operations = members(node.props, 'operations');
  const header = m.padY * 2 + m.rowHeight * (stereotype === undefined ? 1 : 2);
  const compartment = (count: number): number => m.padY * 2 + m.rowHeight * Math.max(1, count);
  let widest = node.label.length * m.charWidth * 1.1 + m.padX * 2;
  if (stereotype !== undefined) {
    widest = Math.max(widest, (stereotype.length + 4) * m.charWidth + m.padX * 2);
  }
  for (const list of [attributes, operations]) {
    const column = list.some((member) => member.visibility !== '') ? m.glyphColumn : 0;
    for (const member of list) {
      widest = Math.max(widest, m.padX * 2 + column + member.text.length * m.charWidth);
    }
  }
  return { w: Math.max(m.minWidth, Math.ceil(widest)), h: header + compartment(attributes.length) + compartment(operations.length) };
}
