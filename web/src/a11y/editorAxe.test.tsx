// @vitest-environment jsdom
/**
 * An automated accessibility check of the editor shell (checklist item 28, WCAG 2.2 AA from spec section 10): axe-core
 * run over the rendered editor in the states a keyboard user passes through. jsdom lays nothing out and paints
 * nothing, so axe's colour-contrast rule cannot run here; chrome contrast is checked when the theme tokens compile
 * instead (web/scripts/build-tokens.ts, themes/README.md). Everything else axe checks in a DOM (names, roles, ARIA
 * attributes and states, landmarks, duplicate ids) runs, WCAG 2.x A and AA rules and best practices alike.
 */

import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import axe from 'axe-core';
import orderService from '../../../docs/examples/order-service.json';
import { parseDocument } from '../model/parse.ts';
import { checkoutSequence } from '../model/sequenceFixture.ts';
import { ImportDrawioDialog } from '../editor/ImportDrawioDialog.tsx';
import { ImportMermaidDialog } from '../editor/ImportMermaidDialog.tsx';
import { ImportSqlDialog } from '../editor/ImportSqlDialog.tsx';
import { ImportVisioDialog } from '../editor/ImportVisioDialog.tsx';
import { MermaidTextDialog } from '../editor/MermaidTextDialog.tsx';
import { DocumentViewer } from '../pages/DocumentViewer.tsx';

const document = parseDocument(orderService);

async function violations(root: Element): Promise<string[]> {
  // axe schedules its work on real timers; the rendering below uses fake animation frames only.
  const result = await axe.run(root, { rules: { 'color-contrast': { enabled: false } }, resultTypes: ['violations'] });
  return result.violations.flatMap((violation) => violation.nodes.map((node) => `${violation.id}: ${node.target.join(' ')}`));
}

describe('editor accessibility (axe-core)', () => {
  beforeEach(() => {
    vi.useFakeTimers({ toFake: ['requestAnimationFrame', 'cancelAnimationFrame'] });
    vi.spyOn(HTMLCanvasElement.prototype, 'getContext').mockReturnValue(null);
    vi.spyOn(HTMLElement.prototype, 'getBoundingClientRect').mockReturnValue(new DOMRect(0, 0, 1200, 800));
    vi.spyOn(console, 'warn').mockImplementation(() => undefined);
  });
  afterEach(() => {
    vi.restoreAllMocks();
    vi.useRealTimers();
  });

  function renderEditor(editable = true) {
    const view = render(<DocumentViewer {...(editable ? { id: document.id } : {})} document={document} etag={'"a11y"'} />);
    act(() => {
      vi.runAllTimers();
    });
    const canvas = view.container.querySelector<HTMLElement>('.diagram-canvas');
    if (!canvas) {
      throw new Error('canvas not rendered');
    }
    return { ...view, canvas };
  }

  it('finds nothing in the editor with a node selected (Tab walk) and the shape panel open', async () => {
    const { container, canvas } = renderEditor();
    fireEvent.keyDown(canvas, { key: 'Tab' });
    fireEvent.keyDown(window, { key: 's' });
    expect(screen.getByRole('searchbox', { name: 'Search shapes' })).toBeTruthy();
    expect(await violations(container)).toEqual([]);
  });

  it('finds nothing in a sequence editor with a message selected (Tab walk) and the Sequence palette group open', async () => {
    const view = render(<DocumentViewer id="doc_checkout" document={checkoutSequence()} etag={'"a11y"'} />);
    act(() => {
      vi.runAllTimers();
    });
    const canvas = view.container.querySelector<HTMLElement>('.diagram-canvas');
    if (!canvas) {
      throw new Error('canvas not rendered');
    }
    // Four participants, then the first message.
    for (let i = 0; i < 5; i++) {
      fireEvent.keyDown(canvas, { key: 'Tab' });
    }
    expect(view.container.querySelector('.selection-edge[data-selection-id="m_1"]')).not.toBeNull();
    fireEvent.keyDown(window, { key: 's' });
    expect(screen.getByRole('button', { name: 'Sequence' })).toBeTruthy();
    expect(await violations(view.container)).toEqual([]);
  });

  it('finds nothing in the Tour panel with a keyframe open, and nothing in Present', async () => {
    const { container } = renderEditor();
    fireEvent.click(screen.getByRole('button', { name: 'Tour' }));
    fireEvent.click(screen.getAllByRole('listitem')[1]?.querySelector('.tour-row-main') ?? container);
    expect(screen.getByLabelText('Caption')).toBeTruthy();
    expect(await violations(container)).toEqual([]);

    fireEvent.keyDown(window, { key: 'Enter', metaKey: true });
    const overlay = screen.getByRole('dialog', { name: 'Presentation' });
    expect(await violations(overlay)).toEqual([]);
    // Playing, where the controls may fade and the caption is a live region.
    fireEvent.keyDown(overlay, { key: 'p' });
    expect(await violations(overlay)).toEqual([]);
  });

  it('finds nothing in the Text pane, with a parse error and a selected line marked, in the DSL view and in the Mermaid view', async () => {
    const text = 'draughtsman 1\ntype architecture\nnode n_web service "Web app"\n';
    vi.stubGlobal('fetch', vi.fn<typeof fetch>((input) => {
      const url = typeof input === 'string' ? input : input instanceof URL ? input.toString() : input.url;
      if (url === '/api/export/dsl') return Promise.resolve(new Response(text));
      if (url === '/api/export/mermaid') return Promise.resolve(new Response('flowchart TD\n', { headers: { 'X-Draughtsman-Export-Warnings': '1', 'X-Draughtsman-Export-Notes': encodeURIComponent('["one simplified shape"]') } }));
      return Promise.resolve(new Response(JSON.stringify({ detail: 'Expected a node id', line: 3, column: 6 }), { status: 422 }));
    }));
    try {
      const { container, canvas } = renderEditor();
      fireEvent.keyDown(canvas, { key: 'Tab' });
      fireEvent.click(screen.getByRole('button', { name: 'Text pane' }));
      vi.useRealTimers();
      const field = await screen.findByRole<HTMLTextAreaElement>('textbox', { name: 'Document DSL' });
      await waitFor(() => { expect(field.value).toBe(text); });
      fireEvent.change(field, { target: { value: `${text}x` } });
      fireEvent.click(screen.getByRole('button', { name: 'Apply' }));
      expect(await screen.findByRole('alert')).toBeTruthy();
      expect(container.querySelector('.dsl-band-selected')).not.toBeNull();
      expect(await violations(container)).toEqual([]);

      fireEvent.click(screen.getByRole('button', { name: 'Mermaid' }));
      expect(await screen.findByRole('list', { name: 'What Mermaid simplified' })).toBeTruthy();
      expect(await violations(container)).toEqual([]);
    } finally {
      vi.unstubAllGlobals();
    }
  });

  it('finds nothing in the Import SQL dialog', async () => {
    const { container } = render(<ImportSqlDialog onClose={() => undefined} onCreated={() => undefined} />);
    expect(screen.getByRole('dialog', { name: 'Import SQL' })).toBeTruthy();
    expect(await violations(container)).toEqual([]);
  });

  it('finds nothing in the Import Mermaid dialog', async () => {
    const { container } = render(<ImportMermaidDialog onClose={() => undefined} onCreated={() => undefined} />);
    expect(screen.getByRole('dialog', { name: 'Import Mermaid' })).toBeTruthy();
    expect(await violations(container)).toEqual([]);
  });

  it('finds nothing in the Import draw.io dialog', async () => {
    const { container } = render(<ImportDrawioDialog onClose={() => undefined} onCreated={() => undefined} />);
    expect(screen.getByRole('dialog', { name: 'Import draw.io' })).toBeTruthy();
    expect(await violations(container)).toEqual([]);
  });

  it('finds nothing in the Import Visio dialog', async () => {
    const { container } = render(<ImportVisioDialog onClose={() => undefined} onCreated={() => undefined} />);
    expect(screen.getByRole('dialog', { name: 'Import Visio' })).toBeTruthy();
    expect(await violations(container)).toEqual([]);
  });

  it('finds nothing in the Export Mermaid fallback dialog', async () => {
    const { container } = render(<MermaidTextDialog text={'flowchart TD\n    a["A"]\n'} onClose={() => undefined} />);
    expect(screen.getByRole('dialog', { name: 'Export Mermaid' })).toBeTruthy();
    expect(await violations(container)).toEqual([]);
  });

  it('finds nothing with the top bar menu open', async () => {
    const { container } = renderEditor();
    fireEvent.click(screen.getByRole('button', { name: 'Menu' }));
    expect(screen.getByRole('menu', { name: 'Menu' })).toBeTruthy();
    expect(await violations(container)).toEqual([]);
  });

  it('finds nothing with the command palette open, or on a read-only preview', async () => {
    const { container } = renderEditor();
    fireEvent.keyDown(window, { key: 'k', metaKey: true });
    expect(screen.getByRole('dialog', { name: 'Command palette' })).toBeTruthy();
    expect(await violations(container)).toEqual([]);
    const preview = renderEditor(false);
    expect(await violations(preview.container)).toEqual([]);
  });

  it('can fail: a control with no name is reported', async () => {
    const { container } = renderEditor();
    screen.getByRole('button', { name: 'Menu' }).removeAttribute('aria-label');
    screen.getByRole('button', { name: 'Menu' }).removeAttribute('title');
    expect(await violations(container)).toContainEqual(expect.stringMatching(/^button-name: /));
  });
});
