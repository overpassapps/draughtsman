import { copyMermaid, copyToClipboard, exportMermaid, exportMermaidDetailed } from './mermaidExport.ts';
import type { Document } from './types.ts';

const document = {
  id: 'doc_seq',
  schemaVersion: 1,
  title: 'Checkout',
  type: 'sequence',
  nodes: [{ id: 'p_a', kind: 'participant', label: 'A', shape: 'rectangle' }],
  edges: [],
  meta: { createdAt: '2026-09-29T00:00:00.000Z', updatedAt: '2026-09-29T00:00:00.000Z', createdBy: 'eric' },
} as unknown as Document;

function urlOf(input: Parameters<typeof fetch>[0]): string {
  return typeof input === 'string' ? input : input instanceof URL ? input.toString() : input.url;
}

const MERMAID = 'sequenceDiagram\n    participant a as A\n';

/** A fetch that answers the export with `MERMAID` and records what it was asked. */
function exportServer(status = 200, body: BodyInit = MERMAID, warnings?: string) {
  const calls: { url: string; init: RequestInit | undefined }[] = [];
  const fetchImpl = vi.fn<typeof fetch>((url, init) => {
    calls.push({ url: urlOf(url), init });
    return Promise.resolve(new Response(body, { status, headers: { 'Content-Type': status === 200 ? 'text/plain' : 'application/problem+json', ...(warnings === undefined ? {} : { 'X-Draughtsman-Export-Warnings': warnings }) } }));
  });
  return { calls, fetchImpl };
}

describe('exportMermaid', () => {
  it('posts the whole document as JSON and returns the text', async () => {
    const { calls, fetchImpl } = exportServer();

    const text = await exportMermaid(document, fetchImpl);

    expect(text).toBe(MERMAID);
    expect(calls.map((c) => c.url)).toEqual(['/api/export/mermaid']);
    expect(calls[0]?.init?.method).toBe('POST');
    expect(JSON.parse(calls[0]?.init?.body as string)).toMatchObject({ id: 'doc_seq', type: 'sequence' });
  });

  it('throws the server’s problem detail on a refusal', async () => {
    const { fetchImpl } = exportServer(422, JSON.stringify({ title: 'The document could not be exported.', detail: 'bad shape' }));

    await expect(exportMermaid(document, fetchImpl)).rejects.toThrow('bad shape');
  });
});

describe('exportMermaidDetailed', () => {
  it('reports how many things the server approximated, from its header', async () => {
    expect(await exportMermaidDetailed(document, exportServer(200, MERMAID, '3').fetchImpl)).toEqual({ text: MERMAID, warnings: 3, notes: [] });
  });

  // P8: what was simplified is listed, not only counted.
  it('reads the list of what was simplified from the notes header, and ignores one it cannot read', async () => {
    const notes = ["n_a: shape 'document' has no Mermaid equivalent; drawn as a rectangle", 'and 3 more (each is a % comment in the text)'];
    const withNotes = (value: string) => vi.fn<typeof fetch>(() => Promise.resolve(new Response(MERMAID, { status: 200, headers: { 'X-Draughtsman-Export-Warnings': '2', 'X-Draughtsman-Export-Notes': value } })));
    expect((await exportMermaidDetailed(document, withNotes(encodeURIComponent(JSON.stringify(notes))))).notes).toEqual(notes);
    expect((await exportMermaidDetailed(document, withNotes('%E0%A4%A'))).notes).toEqual([]);
    expect((await exportMermaidDetailed(document, withNotes(encodeURIComponent('{"not":"a list"}')))).notes).toEqual([]);
  });

  it('reads a missing or unreadable header as a faithful export', async () => {
    expect((await exportMermaidDetailed(document, exportServer().fetchImpl)).warnings).toBe(0);
    expect((await exportMermaidDetailed(document, exportServer(200, MERMAID, 'lots').fetchImpl)).warnings).toBe(0);
  });
});

describe('copyToClipboard', () => {
  it('writes the text with writeText where the browser has no ClipboardItem', async () => {
    const written: string[] = [];
    const writeText = vi.fn((text: string) => {
      written.push(text);
      return Promise.resolve();
    });

    const copied = await copyToClipboard(Promise.resolve('hello'), { writeText } as unknown as Clipboard);

    expect(copied).toBe(true);
    expect(written).toEqual(['hello']);
  });

  it('starts the write before the text exists when it can, which is what Safari needs', async () => {
    class FakeClipboardItem {
      constructor(readonly data: Record<string, Promise<Blob>>) {}
    }
    vi.stubGlobal('ClipboardItem', FakeClipboardItem);
    try {
      let items: FakeClipboardItem[] = [];
      const write = vi.fn((given: FakeClipboardItem[]) => {
        items = given;
        return Promise.resolve();
      });
      let resolveText: (text: string) => void = () => undefined;
      const source = new Promise<string>((resolve) => {
        resolveText = resolve;
      });

      const pending = copyToClipboard(source, { write } as unknown as Clipboard);
      expect(write).toHaveBeenCalledTimes(1); // called with the text still unknown
      resolveText('later');
      expect(await pending).toBe(true);
      expect(await (await items[0]?.data['text/plain'])?.text()).toBe('later');
    } finally {
      vi.unstubAllGlobals();
    }
  });

  it('is false, and does not throw, in a browser with no clipboard', async () => {
    expect(Reflect.get(navigator, 'clipboard')).toBeUndefined();

    expect(await copyToClipboard(Promise.resolve('hello'))).toBe(false);
  });

  it('is false, and does not throw, for a clipboard that refuses', async () => {
    const clipboard = { writeText: () => Promise.reject(new Error('NotAllowedError')) } as unknown as Clipboard;

    expect(await copyToClipboard(Promise.resolve('hello'), clipboard)).toBe(false);
  });

  it('is false, and does not throw, when the export behind it failed', async () => {
    const writeText = vi.fn(() => Promise.resolve());

    expect(await copyToClipboard(Promise.reject(new Error('down')), { writeText } as unknown as Clipboard)).toBe(false);
    expect(writeText).not.toHaveBeenCalled();
  });
});

describe('copyMermaid', () => {
  it('copies the exported text and returns it', async () => {
    const { fetchImpl } = exportServer();
    const written: string[] = [];
    const writeText = vi.fn((text: string) => {
      written.push(text);
      return Promise.resolve();
    });

    const result = await copyMermaid(document, fetchImpl, { writeText } as unknown as Clipboard);

    expect(result).toEqual({ text: MERMAID, copied: true, warnings: 0, notes: [] });
    expect(written).toEqual([MERMAID]);
  });

  it('still returns the text when the clipboard refuses, so the caller can show it', async () => {
    const { fetchImpl } = exportServer();

    const result = await copyMermaid(document, fetchImpl, { writeText: () => Promise.reject(new Error('NotAllowedError')) } as unknown as Clipboard);

    expect(result).toEqual({ text: MERMAID, copied: false, warnings: 0, notes: [] });
  });

  it('carries the warning count so the caller can say the copy is lossy', async () => {
    const writeText = vi.fn(() => Promise.resolve());

    const result = await copyMermaid(document, exportServer(200, MERMAID, '2').fetchImpl, { writeText } as unknown as Clipboard);

    expect(result.warnings).toBe(2);
  });

  it('rejects when the export itself failed', async () => {
    const { fetchImpl } = exportServer(500, JSON.stringify({ detail: 'boom' }));
    const writeText = vi.fn(() => Promise.resolve());

    await expect(copyMermaid(document, fetchImpl, { writeText } as unknown as Clipboard)).rejects.toThrow('boom');
    expect(writeText).not.toHaveBeenCalled();
  });
});
