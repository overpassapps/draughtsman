/**
 * The Mermaid export call (docs/api.md `POST /api/export/mermaid`) and the copy to the clipboard the File menu's
 * Export Mermaid does with it. The text is made by the server (`MermaidExporter`), the one place that knows which
 * shapes and arrowheads Mermaid has, so the browser only asks for it. Copying never loses the text: a browser that
 * refuses the clipboard (no secure context, no permission, a Safari that wants the write inside the click) still hands
 * the text back, and the caller shows it in a dialog to copy by hand.
 */

import { csrfHeaders } from './csrf.ts';
import type { Document } from './types.ts';

async function problemDetail(response: Response, fallback: string): Promise<string> {
  try {
    const problem = (await response.json()) as { detail?: string; title?: string };
    return problem.detail ?? problem.title ?? fallback;
  } catch {
    return fallback;
  }
}

/** The header naming how many things the server had to simplify to write the text (docs/api.md `POST /api/export/mermaid`). */
const WARNINGS_HEADER = 'X-Draughtsman-Export-Warnings';
/** The header listing what it simplified, a percent-encoded JSON array of messages (absent when the export is faithful). */
const NOTES_HEADER = 'X-Draughtsman-Export-Notes';

/** What the export call answered: the text, and how many shapes or arrowheads it could only approximate (0: faithful). */
export interface MermaidExport {
  text: string;
  warnings: number;
  /** What was simplified, in the server's words (QA P8): one entry per thing, so it can be listed, not only counted. */
  notes: string[];
}

function readNotes(header: string | null): string[] {
  if (header === null || header === '') {
    return [];
  }
  try {
    const parsed: unknown = JSON.parse(decodeURIComponent(header));
    return Array.isArray(parsed) ? parsed.filter((note): note is string => typeof note === 'string') : [];
  } catch {
    return [];
  }
}

/**
 * The Mermaid text for `document`: a `sequenceDiagram` for a sequence document, an `erDiagram` for one made only of
 * entity tables, a `flowchart` for anything else.
 */
export async function exportMermaid(document: Document, fetchImpl: typeof fetch = fetch): Promise<string> {
  return (await exportMermaidDetailed(document, fetchImpl)).text;
}

/** `exportMermaid` with the server's count of what the text approximates, so a caller can say the copy is lossy. */
export async function exportMermaidDetailed(document: Document, fetchImpl: typeof fetch = fetch): Promise<MermaidExport> {
  const response = await fetchImpl('/api/export/mermaid', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', Accept: 'text/plain', ...csrfHeaders() },
    body: JSON.stringify(document),
  });
  if (!response.ok) {
    throw new Error(await problemDetail(response, `Export failed: the server returned ${String(response.status)}`));
  }
  const counted = Number.parseInt(response.headers.get(WARNINGS_HEADER) ?? '0', 10);
  return { text: await response.text(), warnings: Number.isFinite(counted) && counted > 0 ? counted : 0, notes: readNotes(response.headers.get(NOTES_HEADER)) };
}

/**
 * Puts `source` on the system clipboard; true when it got there. It hands `ClipboardItem` the pending text where the
 * browser has one (Safari only allows a write that starts inside the click, and the export is a fetch away), and falls
 * back to `writeText`. Never rejects: any refusal, and a failed `source`, are `false`.
 */
export async function copyToClipboard(source: Promise<string>, clipboard?: Clipboard): Promise<boolean> {
  try {
    // The DOM types say the clipboard is always there; outside a secure context it is not.
    const target = clipboard ?? (navigator as { clipboard?: Clipboard }).clipboard;
    if (target === undefined) {
      return false;
    }
    if (typeof ClipboardItem !== 'undefined' && typeof target.write === 'function') {
      const blob = source.then((text) => new Blob([text], { type: 'text/plain' }));
      await target.write([new ClipboardItem({ 'text/plain': blob })]);
      return true;
    }
    await target.writeText(await source);
    return true;
  } catch {
    return false;
  }
}

/** What Export Mermaid produced: the text, whether it is on the clipboard, and how many things it only approximates. */
export interface MermaidCopy {
  text: string;
  copied: boolean;
  warnings: number;
  notes: string[];
}

/** Exports `document` as Mermaid and copies it. Rejects only when the export itself failed; a clipboard refusal is `copied: false`. */
export async function copyMermaid(document: Document, fetchImpl: typeof fetch = fetch, clipboard?: Clipboard): Promise<MermaidCopy> {
  const exported = exportMermaidDetailed(document, fetchImpl);
  const copied = await copyToClipboard(exported.then((result) => result.text), clipboard);
  const { text, warnings, notes } = await exported;
  return { text, copied, warnings, notes };
}
