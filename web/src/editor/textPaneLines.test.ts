import example from '../../../docs/examples/order-service.dsl?raw';
import { elementAtLine, elementLines, lineAtOffset, lineCount, lineRange, selectedLines } from './textPaneLines.ts';

// The shared example, printed by Dsl.Print: its container line, a node with ports and props, edges with ports and waypoints.

describe('where the elements are in the DSL text', () => {
  it('finds the line of every node, container and edge the printer wrote', () => {
    const lines = elementLines(example);
    expect([...lines.entries()]).toEqual([
      ['n_web', 8], ['c_vpc', 9], ['n_api', 10], ['n_db', 11], ['n_cache', 12], ['e_1', 14], ['e_2', 15], ['e_3', 16],
    ]);
    // The numbers are real lines of the text, not a count over the elements.
    expect(example.split('\n')[8]).toMatch(/^container c_vpc /);
    expect(example.split('\n')[14]).toMatch(/^edge e_2 /);
  });

  it('ignores the header, the blank lines and a fly-through keyframe', () => {
    const lines = elementLines(example);
    expect(lines.has('k_overview')).toBe(false);
    expect(lines.has('doc_order_service')).toBe(false);
    expect(elementAtLine(example, 1)).toBeUndefined();
    expect(elementAtLine(example, 7)).toBeUndefined();
    expect(elementAtLine(example, 18)).toBeUndefined();
  });

  it('reads text the person is still typing: indentation, CRLF and a duplicate id', () => {
    const text = 'draughtsman 1\r\n  node a service "A"\r\nedge e1 flow a -> a\r\nnode a service "again"\r\nnode';
    expect([...elementLines(text).entries()]).toEqual([['a', 2], ['e1', 3]]);
    expect(elementAtLine(text, 2)).toBe('a');
    expect(elementAtLine(text, 4)).toBe('a');
    expect(elementAtLine(text, 5)).toBeUndefined();
  });

  it('knows nothing about a line that is not there', () => {
    expect(elementAtLine(example, 0)).toBeUndefined();
    expect(elementAtLine(example, 500)).toBeUndefined();
  });

  it('turns a selection into ascending lines, skipping ids the text does not declare', () => {
    expect(selectedLines(example, new Set(['e_2', 'n_web', 'gone']))).toEqual([8, 15]);
    expect(selectedLines(example, new Set())).toEqual([]);
  });

  it('counts lines the way a text area shows them', () => {
    expect(lineCount('')).toBe(1);
    expect(lineCount('a\nb')).toBe(2);
    expect(lineCount('a\nb\n')).toBe(3);
    expect(lineCount('a\r\nb')).toBe(2);
  });

  it('maps a caret offset to its line and a line to its character range', () => {
    const text = 'one\ntwo\r\nthree';
    expect([0, 3, 4, 7, 9, 13].map((offset) => lineAtOffset(text, offset))).toEqual([1, 1, 2, 2, 3, 3]);
    expect(lineAtOffset(text, 999)).toBe(3);
    expect(lineAtOffset(text, -4)).toBe(1);
    expect(lineRange(text, 1)).toEqual({ start: 0, end: 3 });
    expect(lineRange(text, 2)).toEqual({ start: 4, end: 7 });
    expect(lineRange(text, 3)).toEqual({ start: 9, end: 14 });
    expect(lineRange(text, 9)).toEqual({ start: 14, end: 14 });
  });
});
