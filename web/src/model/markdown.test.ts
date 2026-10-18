import { parseInline, parseMarkdown } from './markdown.ts';

describe('parseMarkdown', () => {
  it('reads headings, paragraphs, lists and fenced code', () => {
    const blocks = parseMarkdown('# Title\n\nIntro line.\n\n- one\n- two\n\n1. first\n\n```sh\necho hi\n```\n');

    expect(blocks.map((block) => block.kind)).toEqual(['heading', 'paragraph', 'listItem', 'listItem', 'listItem', 'code']);
    const heading = blocks[0];
    expect(heading?.kind === 'heading' && heading.level).toBe(1);
    const code = blocks[5];
    expect(code?.kind === 'code' && code.text).toBe('echo hi');
  });

  it('joins consecutive lines into one paragraph', () => {
    const blocks = parseMarkdown('first line\nsecond line\n');

    expect(blocks).toHaveLength(1);
    const paragraph = blocks[0];
    expect(paragraph?.kind === 'paragraph' && paragraph.tokens).toEqual([{ kind: 'text', text: 'first line second line' }]);
  });

  // Two-sided: the same bracketed token is a citation when the diagram has that id and plain text when it does not,
  // so a renderer cannot make every bracket clickable (or none).
  it('makes a bracketed id a citation only when the diagram has that id', () => {
    const known = new Set(['n_cart']);

    const citation = parseInline('See [n_cart] here.', known);
    expect(citation).toContainEqual({ kind: 'citation', id: 'n_cart' });

    const text = parseInline('See [n_other] here.', known);
    expect(text).toEqual([{ kind: 'text', text: 'See [n_other] here.' }]);
  });

  it('does not read bold or citations inside an inline code span', () => {
    expect(parseInline('`[n_cart] **text**`', new Set(['n_cart']))).toEqual([{ kind: 'code', text: '[n_cart] **text**' }]);
  });

  it('keeps raw HTML in the model output as text, never as markup', () => {
    const tokens = parseInline('<script>alert(1)</script>', new Set());

    expect(tokens).toEqual([{ kind: 'text', text: '<script>alert(1)</script>' }]);
  });
});
