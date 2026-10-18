// @vitest-environment jsdom
import { createDomLineBreaker, estimateTextWidth, labelFont, linesFromLayout, wrapText } from './text.ts';

const font = labelFont('Inter, sans-serif');
// A fixed-width measurer makes the expected breaks obvious: every character is 10 units wide.
const tenPerChar = (text: string): number => text.length * 10;

describe('wrapText', () => {
  it('keeps short text on one line', () => {
    expect(wrapText('Orders API', 1000, font, tenPerChar)).toEqual(['Orders API']);
  });

  it('breaks at spaces to fit the width', () => {
    expect(wrapText('the quick brown fox', 90, font, tenPerChar)).toEqual(['the quick', 'brown fox']);
  });

  it('honours explicit newlines and keeps spaces as typed', () => {
    expect(wrapText('one\ntwo   three', 1000, font, tenPerChar)).toEqual(['one', 'two   three']);
    expect(wrapText('  indented\r\n\nlast', 1000, font, tenPerChar)).toEqual(['  indented', '', 'last']);
  });

  it('leaves the spaces a line wraps at off it, hanging, and keeps the spaces that end a paragraph', () => {
    // "aaaa " would be 50 wide: the space hangs, so "aaaa" still breaks where it would have without it.
    expect(wrapText('aaaa   bbbb  ', 40, font, tenPerChar)).toEqual(['aaaa', 'bbbb  ']);
    expect(wrapText('ab cd\nef  \ngh', 30, font, tenPerChar)).toEqual(['ab', 'cd', 'ef  ', 'gh']);
    // Spaces alone never force a break, however many there are.
    expect(wrapText('a          b', 30, font, tenPerChar)).toEqual(['a', 'b']);
  });

  it('breaks the same text differently at each width, every line fitting it', () => {
    const text = 'Validate the payment before the order ships';
    const byWidth = new Map<number, string[]>();
    for (const width of [90, 120, 160, 200, 450]) {
      const lines = wrapText(text, width, font, tenPerChar);
      for (const line of lines) {
        expect(tenPerChar(line)).toBeLessThanOrEqual(width);
      }
      // No word is wider than 90, so nothing is split and nothing but the spaces at the breaks is lost.
      expect(lines.join(' ')).toBe(text);
      byWidth.set(width, lines);
    }
    expect(byWidth.get(90)).toEqual(['Validate', 'the', 'payment', 'before', 'the order', 'ships']);
    expect(byWidth.get(200)).toEqual(['Validate the payment', 'before the order', 'ships']);
    expect(byWidth.get(450)).toEqual([text]);
  });

  it('splits an overlong word only once the words before it have had their own line', () => {
    expect(wrapText('to abcdefgh ij', 40, font, tenPerChar)).toEqual(['to', 'abcd', 'efgh', 'ij']);
    expect(wrapText('abcdefgh ij', 50, font, tenPerChar)).toEqual(['abcde', 'fgh', 'ij']);
  });

  it('splits a single word that is wider than a line', () => {
    expect(wrapText('abcdefgh', 30, font, tenPerChar)).toEqual(['abc', 'def', 'gh']);
  });

  it('returns one empty line for empty input', () => {
    expect(wrapText('', 100, font, tenPerChar)).toEqual(['']);
  });

  it('wraps with the estimating measurer', () => {
    const lines = wrapText('Customer relationship management platform', 120, font);
    expect(lines.length).toBeGreaterThan(1);
    for (const line of lines) {
      expect(estimateTextWidth(line, font)).toBeLessThanOrEqual(120);
    }
  });
});

/**
 * A stand-in for the browser's layout of one paragraph: the line each character landed on, as the lines `wrapText`
 * makes, with the spaces each line wrapped at hanging on that line (which is where a browser puts them).
 */
function layoutOf(paragraph: string, lines: readonly string[]): number[] {
  const lineOfChar: number[] = [];
  let at = 0;
  lines.forEach((line, i) => {
    for (let k = 0; k < line.length; k++, at++) {
      lineOfChar.push(i);
    }
    while (i < lines.length - 1 && paragraph[at] === ' ') {
      lineOfChar.push(i);
      at++;
    }
  });
  expect(at).toBe(paragraph.length);
  return lineOfChar;
}

describe('linesFromLayout', () => {
  const texts = [
    'the quick brown fox jumps over the lazy dog',
    'Customer   relationship  management platform  ',
    'supercalifragilisticexpialidocious and more',
    '  leading spaces then a veryveryverylongword',
    'emoji 🚀🚀🚀 rockets 🚀 and ünïcödé',
  ];

  it.each([30, 50, 80, 120, 1000])('reads back exactly the lines a layout put the paragraph on, at width %i', (width) => {
    for (const text of texts) {
      const expected = wrapText(text, width, font, tenPerChar);
      const lineOfChar = layoutOf(text, expected);
      let probes = 0;
      const lines = linesFromLayout(text, (start, end) => {
        probes += 1;
        // A probe always covers exactly one code point.
        expect(text.slice(start, end)).toMatch(/^[\s\S]$/u);
        return (lineOfChar[start] ?? -1) * 18.2 + 10.5;
      });
      expect(lines).toEqual(expected);
      // Bisection: a handful of probes per line, never one per character of a long paragraph.
      expect(probes).toBeLessThanOrEqual(expected.length * (Math.ceil(Math.log2(text.length)) + 2));
    }
  });

  it('keeps an empty paragraph as one empty line', () => {
    expect(linesFromLayout('', () => 0)).toEqual(['']);
  });

  it('can fail: a layout that breaks elsewhere reads back as those lines, not as wrapText would have it', () => {
    // A browser that broke after the hyphen, as Chrome does.
    const text = 'e-commerce platform';
    const lines = linesFromLayout(text, (start) => (start < 2 ? 0 : start < 11 ? 1 : 2));
    expect(lines).toEqual(['e-', 'commerce', 'platform']);
    expect(wrapText(text, 100, font, tenPerChar)).not.toEqual(lines);
  });
});

describe('createDomLineBreaker', () => {
  it('declines where nothing is laid out, so the renderer falls back to wrapText, and leaves nothing in the page', () => {
    const before = document.body.innerHTML;
    expect(createDomLineBreaker()).toBeUndefined();
    expect(document.body.innerHTML).toBe(before);
  });
});

describe('estimateTextWidth', () => {
  it('scales with font size and treats narrow letters as narrower', () => {
    expect(estimateTextWidth('abc', '28px x')).toBeCloseTo(estimateTextWidth('abc', '14px x') * 2);
    expect(estimateTextWidth('iii', font)).toBeLessThan(estimateTextWidth('mmm', font));
  });
});

// The server renderer wraps node labels with a C# port of wrapText and estimateTextWidth at the 13px it draws them in
// (src/Draughtsman.Mcp/Rendering/NodeLabelLayout.cs). NodeLabelLayoutTests pins these same strings and widths; change a
// rule on either side and both suites must move together.
describe('server renderer parity', () => {
  const server = labelFont('sans-serif', 13);

  it('breaks the strings the server tests pin at the same places', () => {
    expect(wrapText('Supercalifragilisticexpialidocious', 100, server)).toEqual(['Supercalifragilis', 'ticexpialidociou', 's']);
    expect(wrapText('Collect feeds: RSS from four crypto outlets, Fear & Greed index, CryptoPanic if keyed', 234, server)).toEqual([
      'Collect feeds: RSS from four crypto',
      'outlets, Fear & Greed index,',
      'CryptoPanic if keyed',
    ]);
    expect(wrapText('Hold; check again next minute', 80, server)).toEqual(['Hold; check', 'again next', 'minute']);
    expect(wrapText('Plan well-formed\nand within bounds?', 100, server)).toEqual(['Plan', 'well-formed', 'and within', 'bounds?']);
    expect(wrapText('  leading  and trailing  ', 90, server)).toEqual(['  leading  and', 'trailing  ']);
    expect(wrapText('', 50, server)).toEqual(['']);
    expect(wrapText('ab', 1, server)).toEqual(['a', 'b']);
  });

  it('estimates the widths the server tests pin', () => {
    expect(estimateTextWidth('Plan well-formed', server)).toBeCloseTo(106.73, 2);
    expect(estimateTextWidth('WWW', server)).toBeCloseTo(35.1, 2);
    expect(estimateTextWidth('iii', server)).toBeCloseTo(12.48, 2);
    expect(estimateTextWidth('0123', server)).toBeCloseTo(30.16, 2);
    expect(estimateTextWidth('a b', server)).toBeCloseTo(17.94, 2);
  });
});
