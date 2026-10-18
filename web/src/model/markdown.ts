/**
 * A deliberately small Markdown reader for the AI panel's "Diagram to document" output. It renders the subset a
 * language model reliably produces - headings, paragraphs, bulleted and numbered lists, fenced code blocks, bold
 * and inline code - and turns a bracketed id the diagram actually has (`[n_checkout]`) into a citation token the
 * panel can make clickable. Blocks and inline tokens are returned as plain data and rendered as React text, so no
 * HTML from the model is ever interpreted: this path never touches `dangerouslySetInnerHTML`.
 */

export type InlineToken =
  | { kind: 'text'; text: string }
  | { kind: 'strong'; text: string }
  | { kind: 'code'; text: string }
  | { kind: 'citation'; id: string };

export interface MarkdownHeading { kind: 'heading'; level: number; tokens: InlineToken[] }
export interface MarkdownParagraph { kind: 'paragraph'; tokens: InlineToken[] }
export interface MarkdownListItem { kind: 'listItem'; ordered: boolean; marker: string; tokens: InlineToken[] }
export interface MarkdownCode { kind: 'code'; text: string }
export type MarkdownBlock = MarkdownHeading | MarkdownParagraph | MarkdownListItem | MarkdownCode;

const HEADING = /^(#{1,6})\s+(.*?)\s*#*\s*$/;
const BULLET = /^\s{0,3}[-*+]\s+(.*)$/;
const ORDERED = /^\s{0,3}(\d+)[.)]\s+(.*)$/;
const ID = /^[A-Za-z0-9_-]+$/;

/** Parses the model's Markdown. A bracketed token becomes a citation only when `knownIds` contains it. */
export function parseMarkdown(markdown: string, knownIds: ReadonlySet<string> = new Set<string>()): MarkdownBlock[] {
  const blocks: MarkdownBlock[] = [];
  const lines = markdown.replace(/\r\n/g, '\n').split('\n');
  let paragraph: string[] = [];
  let index = 0;

  const flushParagraph = (): void => {
    if (paragraph.length > 0) {
      blocks.push({ kind: 'paragraph', tokens: parseInline(paragraph.join(' '), knownIds) });
      paragraph = [];
    }
  };

  while (index < lines.length) {
    const line = lines[index] ?? '';
    if (/^\s*```/.test(line)) {
      flushParagraph();
      index += 1;
      const body: string[] = [];
      while (index < lines.length && !/^\s*```/.test(lines[index] ?? '')) {
        body.push(lines[index] ?? '');
        index += 1;
      }
      index += 1;
      blocks.push({ kind: 'code', text: body.join('\n') });
      continue;
    }

    if (line.trim().length === 0) {
      flushParagraph();
      index += 1;
      continue;
    }

    const heading = HEADING.exec(line);
    if (heading !== null) {
      flushParagraph();
      blocks.push({ kind: 'heading', level: (heading[1] ?? '#').length, tokens: parseInline(heading[2] ?? '', knownIds) });
      index += 1;
      continue;
    }

    const ordered = ORDERED.exec(line);
    if (ordered !== null) {
      flushParagraph();
      blocks.push({ kind: 'listItem', ordered: true, marker: `${ordered[1] ?? ''}.`, tokens: parseInline(ordered[2] ?? '', knownIds) });
      index += 1;
      continue;
    }

    const bullet = BULLET.exec(line);
    if (bullet !== null) {
      flushParagraph();
      blocks.push({ kind: 'listItem', ordered: false, marker: '\u2022', tokens: parseInline(bullet[1] ?? '', knownIds) });
      index += 1;
      continue;
    }

    paragraph.push(line.trim());
    index += 1;
  }

  flushParagraph();
  return blocks;
}

/**
 * Splits one line into text, `**bold**`, `` `code` `` and citation tokens. Bold and citations are not recognised
 * inside a code span, which matches how Markdown reads.
 */
export function parseInline(text: string, knownIds: ReadonlySet<string> = new Set<string>()): InlineToken[] {
  const tokens: InlineToken[] = [];
  let buffer = '';
  let index = 0;

  const flush = (): void => {
    if (buffer.length > 0) {
      tokens.push({ kind: 'text', text: buffer });
      buffer = '';
    }
  };

  while (index < text.length) {
    const character = text[index] ?? '';
    if (character === '`') {
      const end = text.indexOf('`', index + 1);
      if (end > index) {
        flush();
        tokens.push({ kind: 'code', text: text.slice(index + 1, end) });
        index = end + 1;
        continue;
      }
    }
    if (character === '*' && text[index + 1] === '*') {
      const end = text.indexOf('**', index + 2);
      if (end > index + 2) {
        flush();
        tokens.push({ kind: 'strong', text: text.slice(index + 2, end) });
        index = end + 2;
        continue;
      }
    }
    if (character === '[') {
      const end = text.indexOf(']', index + 1);
      if (end > index + 1) {
        const id = text.slice(index + 1, end);
        if (knownIds.has(id) && ID.test(id)) {
          flush();
          tokens.push({ kind: 'citation', id });
          index = end + 1;
          continue;
        }
      }
    }
    buffer += character;
    index += 1;
  }

  flush();
  return tokens;
}
