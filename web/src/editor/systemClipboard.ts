/**
 * Carries a copied selection (model/clipboard.ts) through the system clipboard as JSON text, so a copy in one open
 * document pastes into another tab (checklist item 19), with an in-memory fallback so a paste in the same tab works
 * even when the browser refuses clipboard access.
 *
 * Writing always records the text in memory, then puts it on the DataTransfer of the native `copy` or `cut` event
 * that asked for it: the browser takes that without a permission prompt or a secure context. Only without an event (a
 * browser that fires none on a non-editable element) does it try `navigator.clipboard.writeText`. Reading likewise
 * prefers the text a native `paste` event carries, since `navigator.clipboard.readText` makes Chrome ask first and
 * waits, unanswered, until the user does; without an event it asks `readText`. Either way the system clipboard holds
 * whatever the user copied last, here or anywhere else, so text that is not a Draughtsman selection pastes nothing. The
 * paste falls back to the text this tab last wrote only when there is no text to read at all: the read is refused
 * (permission denied, no Clipboard API, an insecure context).
 *
 * A payload read back is checked against the document schema, wrapped in a throwaway document, before anything
 * trusts it: the text may have come from anywhere, and a malformed node written into the Y.Doc would break the editor.
 */

import { CLIPBOARD_FORMAT, CLIPBOARD_VERSION, type ClipboardPayload } from '../model/clipboard.ts';
import { isDocument } from '../model/parse.ts';
import type { Document } from '../model/types.ts';

/** The part of the async Clipboard API used here; tests inject a fake. */
export type TextClipboard = Pick<Clipboard, 'readText' | 'writeText'>;

export interface ClipboardStore {
  /**
   * Records the payload in memory and on `transfer`, the DataTransfer of a native copy or cut event, before returning;
   * without one, on the system clipboard when the browser allows it. Never rejects.
   */
  write: (payload: ClipboardPayload, transfer?: DataTransfer) => Promise<void>;
  /**
   * The Draughtsman selection on the clipboard, or undefined when it holds anything else. `eventText` is the text a
   * native paste event carried; without it the system clipboard is read. Never rejects.
   */
  read: (eventText?: string) => Promise<ClipboardPayload | undefined>;
}

/** The payload a clipboard text encodes, or undefined when the text is anything but a valid Draughtsman selection. */
export function parseClipboardText(text: string): ClipboardPayload | undefined {
  let value: unknown;
  try {
    value = JSON.parse(text);
  } catch {
    return undefined;
  }
  if (typeof value !== 'object' || value === null) {
    return undefined;
  }
  const { format, version, source, nodes, edges } = value as Partial<Record<keyof ClipboardPayload, unknown>>;
  if (format !== CLIPBOARD_FORMAT || version !== CLIPBOARD_VERSION || (source !== undefined && typeof source !== 'string')) {
    return undefined;
  }
  const shell: Omit<Document, 'nodes' | 'edges'> & { nodes: unknown; edges: unknown } = {
    id: 'clipboard',
    schemaVersion: 1,
    title: 'Clipboard',
    type: 'flowchart',
    nodes,
    edges,
    meta: { createdAt: '1970-01-01T00:00:00.000Z', updatedAt: '1970-01-01T00:00:00.000Z', createdBy: 'clipboard' },
  };
  if (!isDocument(shell)) {
    return undefined;
  }
  return { format: CLIPBOARD_FORMAT, version: CLIPBOARD_VERSION, ...(source === undefined ? {} : { source }), nodes: shell.nodes, edges: shell.edges };
}

export function createClipboardStore(system: () => TextClipboard | undefined = () => globalThis.navigator.clipboard): ClipboardStore {
  let memory: string | undefined;
  return {
    write: async (payload, transfer) => {
      const text = JSON.stringify(payload);
      memory = text;
      if (transfer !== undefined) {
        transfer.setData('text/plain', text);
        return;
      }
      try {
        await system()?.writeText(text);
      } catch {
        // Refused: the in-memory copy still serves a paste in this tab whose clipboard read is refused too.
      }
    },
    read: async (eventText) => {
      let text = eventText;
      if (text === undefined) {
        try {
          text = await system()?.readText();
        } catch {
          text = undefined;
        }
      }
      return parseClipboardText(text ?? memory ?? '');
    },
  };
}

/** The one clipboard every editor in the tab shares, so copy in one document and paste in the next works in-tab too. */
export const sessionClipboard: ClipboardStore = createClipboardStore();
