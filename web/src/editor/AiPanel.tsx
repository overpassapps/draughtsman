import { useEffect, useMemo, useRef, useState } from 'react';
import {
  diagramToDocument,
  DiagramToDocumentFailed,
  generateFromText,
  GenerateFromTextFailed,
  type DiagramToDocumentOutputKind,
  type GeneratableDiagramType,
  type GenerateFromTextResult,
} from '../model/documentClient.ts';
import { downloadBlob, exportFileName } from './exportDiagram.tsx';
import { MarkdownView } from './MarkdownView.tsx';
import type { Document } from '../model/types.ts';

interface Props {
  document: Document;
  /** False while the tab is hidden: no request is sent and the draft (if any) is kept for when it reopens. */
  active: boolean;
  onApply: (next: Document, base: Document) => void;
  onEscape: () => void;
  /** Selects and reveals a node on the canvas; used when a cited node id in a generated document is clicked. */
  onRevealNode?: ((id: string) => void) | undefined;
}

type AiTab = 'from-text' | 'to-document';

const DIAGRAM_TYPES: readonly { value: GeneratableDiagramType; label: string }[] = [
  { value: 'flowchart', label: 'Flowchart' },
  { value: 'architecture', label: 'Architecture' },
  { value: 'sequence', label: 'Sequence' },
];

const OUTPUT_KINDS: readonly { value: DiagramToDocumentOutputKind; label: string }[] = [
  { value: 'design-doc', label: 'Design doc' },
  { value: 'runbook', label: 'Runbook' },
  { value: 'explanation', label: 'Plain explanation' },
];

/**
 * The AI panel (build-spec-v0.1.md section 9; design/atlas/ai-panel.md): two tabs. "From text" (MVP AI feature 1)
 * turns a description into a draft, applied to the document through `onApply`. "To document" (MVP AI feature 2)
 * turns the diagram's outline into Markdown - a design doc, runbook or explanation - that cites the diagram's
 * elements by id; it writes nothing to the document, so it only displays the result and offers Copy and Download.
 */
export function AiPanel({ document, active, onApply, onEscape, onRevealNode }: Props) {
  const [tab, setTab] = useState<AiTab>('from-text');
  const [description, setDescription] = useState('');
  const [diagramType, setDiagramType] = useState<GeneratableDiagramType>(document.type === 'freeform' ? 'flowchart' : document.type);
  const [working, setWorking] = useState(false);
  const [error, setError] = useState<string>();
  const [notice, setNotice] = useState<string>();
  const [outputKind, setOutputKind] = useState<DiagramToDocumentOutputKind>('design-doc');
  const [markdown, setMarkdown] = useState<string>();
  const field = useRef<HTMLTextAreaElement>(null);

  const nodeIds = useMemo(() => new Set(document.nodes.map((node) => node.id)), [document]);
  // A citation reads as the node's own label ("Sign in"), not the id the model wrote it with ("n_sign_in").
  const nodeLabels = useMemo(() => new Map(document.nodes.filter((node) => node.label.trim() !== '').map((node) => [node.id, node.label] as const)), [document]);
  useEffect(() => { if (active && tab === 'from-text') field.current?.focus(); }, [active, tab]);

  const generate = async (): Promise<void> => {
    const prompt = description.trim();
    if (prompt.length === 0 || working) {
      return;
    }

    setWorking(true);
    setError(undefined);
    setNotice(undefined);
    const base = document;
    try {
      const result = await generateFromText(prompt, diagramType, base.id);
      onApply(result.document, base);
      setNotice(summarize(result));
      setDescription('');
    } catch (caught) {
      setError(describeError(caught));
    } finally {
      setWorking(false);
    }
  };

  const generateDocument = async (): Promise<void> => {
    if (working) {
      return;
    }

    setWorking(true);
    setError(undefined);
    setMarkdown(undefined);
    try {
      const result = await diagramToDocument(document.id, outputKind);
      setMarkdown(result.markdown);
    } catch (caught) {
      setError(describeDocumentError(caught));
    } finally {
      setWorking(false);
    }
  };

  return (
    <section className="ai-panel" aria-label="AI" onKeyDown={(event) => {
      event.stopPropagation();
      if (event.key === 'Escape') { event.preventDefault(); onEscape(); }
      if ((event.key === 'Enter') && (event.metaKey || event.ctrlKey)) {
        event.preventDefault();
        void (tab === 'from-text' ? generate() : generateDocument());
      }
    }}>
      <div className="ai-tabs" role="tablist" aria-label="AI tools">
        <button type="button" role="tab" aria-selected={tab === 'from-text'} onClick={() => { setTab('from-text'); setError(undefined); }}>From text</button>
        <button type="button" role="tab" aria-selected={tab === 'to-document'} onClick={() => { setTab('to-document'); setError(undefined); }}>To document</button>
      </div>

      {tab === 'from-text' && (
        <>
          <label htmlFor="ai-prompt">Describe the diagram</label>
          <textarea id="ai-prompt" ref={field} value={description} disabled={working}
            placeholder="A paragraph, notes, or a pasted Mermaid block…"
            onChange={(event) => { setDescription(event.target.value); setError(undefined); }} />
          <fieldset className="ai-diagram-type">
            <legend>Diagram type</legend>
            {DIAGRAM_TYPES.map(({ value, label }) => (
              <label key={value}>
                <input type="radio" name="ai-diagram-type" value={value} checked={diagramType === value} disabled={working}
                  onChange={() => { setDiagramType(value); }} />
                {label}
              </label>
            ))}
          </fieldset>
          <button type="button" disabled={working || description.trim().length === 0} onClick={() => { void generate(); }}>
            {working ? 'Generating…' : 'Generate'}
          </button>
          {working && <p role="status">Generating…</p>}
          {error !== undefined && <p role="alert">{error}</p>}
          {notice !== undefined && <p role="status">{notice}</p>}
          <p className="ai-privacy-note">Only your prompt, and this diagram's whole current content when updating it, go to the model. Generated content can be wrong; check it before you share it.</p>
        </>
      )}

      {tab === 'to-document' && (
        <>
          <label htmlFor="ai-output-kind">Output</label>
          <select id="ai-output-kind" value={outputKind} disabled={working}
            onChange={(event) => { setOutputKind(event.target.value as DiagramToDocumentOutputKind); setError(undefined); }}>
            {OUTPUT_KINDS.map(({ value, label }) => <option key={value} value={value}>{label}</option>)}
          </select>
          <button type="button" disabled={working} onClick={() => { void generateDocument(); }}>
            {working ? 'Generating…' : 'Generate'}
          </button>
          {working && <p role="status">Generating…</p>}
          {error !== undefined && <p role="alert">{error}</p>}
          {markdown !== undefined && (
            <div className="ai-document">
              <div className="ai-document-actions">
                <button type="button" onClick={() => { void copyText(markdown); }}>Copy</button>
                <button type="button" onClick={() => { downloadBlob(exportFileName(document.title, 'md'), new Blob([markdown], { type: 'text/markdown' })); }}>Download</button>
              </div>
              <MarkdownView markdown={markdown} nodeIds={nodeIds} labels={nodeLabels} onCitation={(id) => { onRevealNode?.(id); }} />
            </div>
          )}
          <p className="ai-privacy-note">Only this diagram's outline (its ids, kinds and labels — never geometry, style or images) is sent to the model. Generated content can be wrong; check it before you share it.</p>
        </>
      )}
    </section>
  );
}

/** Copies the generated Markdown. The async clipboard needs a secure context, so fall back to a selection and the native copy command on self-hosted HTTP. */
async function copyText(text: string): Promise<void> {
  try {
    // The DOM type says `navigator.clipboard` always exists, but it is genuinely absent outside a secure context
    // (a self-hosted instance on a plain-HTTP LAN address), so the capability is read rather than assumed.
    const clipboard = (navigator as { clipboard?: Clipboard }).clipboard;
    if (clipboard !== undefined) {
      await clipboard.writeText(text);
      return;
    }
  } catch {
    // Fall through to the native command below.
  }

  const area = document.createElement('textarea');
  area.value = text;
  area.setAttribute('readonly', '');
  area.style.position = 'fixed';
  area.style.opacity = '0';
  document.body.appendChild(area);
  area.select();
  try {
    // eslint-disable-next-line @typescript-eslint/no-deprecated
    document.execCommand('copy');
  } finally {
    area.remove();
  }
}

/** Added/changed/removed counts (build-spec-v0.1.md section 9): a fresh document is all "added"; updating an existing one can also change or remove something already there, which is worth calling out since it was not asked for by id. */
function summarize(result: GenerateFromTextResult): string {
  const added = result.addedNodeIds.length + result.addedEdgeIds.length;
  const parts = [`${String(added)} added`];
  if (result.changedElementCount > 0) parts.push(`${String(result.changedElementCount)} changed`);
  if (result.removedElementCount > 0) parts.push(`${String(result.removedElementCount)} removed`);
  const warnings = result.warnings.length > 0 ? ` ${String(result.warnings.length)} warning${result.warnings.length === 1 ? '' : 's'}.` : '';
  return `${parts.join(', ')} from ${result.provenance.provider}/${result.provenance.model}.${warnings}`;
}

function describeError(caught: unknown): string {
  if (caught instanceof GenerateFromTextFailed) {
    return caught.message.length > 0 ? caught.message : `The request failed (${String(caught.status)}).`;
  }

  return caught instanceof Error ? caught.message : 'Could not generate a diagram.';
}

function describeDocumentError(caught: unknown): string {
  if (caught instanceof DiagramToDocumentFailed) {
    return caught.message.length > 0 ? caught.message : `The request failed (${String(caught.status)}).`;
  }

  return caught instanceof Error ? caught.message : 'Could not generate a document.';
}
