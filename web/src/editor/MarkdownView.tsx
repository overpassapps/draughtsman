import type { JSX } from 'react';
import { parseMarkdown, type InlineToken, type MarkdownBlock } from '../model/markdown.ts';

interface Props {
  markdown: string;
  /** The diagram's node ids: a bracketed token that is one of these renders as a citation button. */
  nodeIds: ReadonlySet<string>;
  /** The diagram's node labels by id: a citation is shown as the node's label, not its id (QA P9). Falls back to the id. */
  labels?: ReadonlyMap<string, string>;
  /** Called when a citation is clicked: selects and reveals that node on the canvas. */
  onCitation: (id: string) => void;
}

/**
 * Renders the model's Markdown for "Diagram to document" (design/atlas/ai-panel.md). Every token is React text, so
 * nothing in the model's output is parsed as HTML; only a citation - a bracketed id the diagram actually has - is
 * interactive, and it is a real button with an accessible name naming the node it selects.
 */
export function MarkdownView({ markdown, nodeIds, labels, onCitation }: Props) {
  const blocks = parseMarkdown(markdown, nodeIds);
  return (
    <div className="ai-document-body">
      {blocks.map((block, index) => renderBlock(block, index, onCitation, labels))}
    </div>
  );
}

function renderBlock(block: MarkdownBlock, key: number, onCitation: (id: string) => void, labels: ReadonlyMap<string, string> | undefined): JSX.Element {
  switch (block.kind) {
    case 'heading': {
      const Tag = `h${String(block.level)}` as 'h1' | 'h2' | 'h3' | 'h4' | 'h5' | 'h6';
      return <Tag key={key}>{renderInline(block.tokens, onCitation, labels)}</Tag>;
    }
    case 'code':
      return <pre key={key}><code>{block.text}</code></pre>;
    case 'listItem':
      return (
        <p key={key} className="ai-document-item">
          <span aria-hidden="true">{block.marker} </span>
          {renderInline(block.tokens, onCitation, labels)}
        </p>
      );
    default:
      return <p key={key}>{renderInline(block.tokens, onCitation, labels)}</p>;
  }
}

function renderInline(tokens: readonly InlineToken[], onCitation: (id: string) => void, labels: ReadonlyMap<string, string> | undefined): JSX.Element[] {
  return tokens.map((token, index) => {
    switch (token.kind) {
      case 'strong':
        return <strong key={index}>{token.text}</strong>;
      case 'code':
        return <code key={index}>{token.text}</code>;
      case 'citation':
        return (
          <button key={index} type="button" className="ai-citation" title={`Show ${labels?.get(token.id) ?? token.id} on the canvas`} onClick={() => { onCitation(token.id); }}>
            {labels?.get(token.id) ?? token.id}
          </button>
        );
      default:
        return <span key={index}>{token.text}</span>;
    }
  });
}
