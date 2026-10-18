// @vitest-environment jsdom
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import example from '../../../docs/examples/order-service.json';
import { parseDocument } from '../model/parse.ts';
import { checkoutSequence } from '../model/sequenceFixture.ts';
import { AiPanel } from './AiPanel.tsx';

const original = parseDocument(example);
const generated = { ...original, nodes: [...original.nodes, { id: 'n_generated', kind: 'service', label: 'Generated', shape: 'rectangle' }] };

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function setup(responder: () => Promise<Response> = () => Promise.resolve(json({
  document: generated, warnings: [], addedNodeIds: ['n_generated'], addedEdgeIds: [], changedElementCount: 0, removedElementCount: 0, provenance: { provider: 'ollama', model: 'llama3.1' },
}))) {
  const fetchMock = vi.fn<typeof fetch>(responder);
  vi.stubGlobal('fetch', fetchMock);
  const onApply = vi.fn();
  const onEscape = vi.fn();
  const rendered = render(<AiPanel document={original} active onApply={onApply} onEscape={onEscape} />);
  return { ...rendered, fetchMock, onApply, onEscape };
}

afterEach(() => { vi.unstubAllGlobals(); });

function typePrompt(text: string): void {
  fireEvent.change(screen.getByRole('textbox', { name: 'Describe the diagram' }), { target: { value: text } });
}

it('disables Generate until a prompt is entered', () => {
  setup();
  const button = screen.getByRole<HTMLButtonElement>('button', { name: 'Generate' });
  expect(button.disabled).toBe(true);
  typePrompt('a login flow');
  expect(button.disabled).toBe(false);
});

it('sends the prompt, the current diagram type and the document id, then applies the result as one call', async () => {
  const { onApply, fetchMock } = setup();
  typePrompt('a login flow');
  fireEvent.click(screen.getByRole('button', { name: 'Generate' }));

  await waitFor(() => { expect(onApply).toHaveBeenCalledWith(generated, original); });
  const [url, init] = fetchMock.mock.calls[0] ?? [];
  expect(url).toBe('/api/ai/generate-from-text');
  expect(JSON.parse(init?.body as string)).toEqual({ description: 'a login flow', diagramType: 'architecture', documentId: original.id });
});

// Two-sided: the notice must actually reflect the counts the server sent, not a fixed "added" phrasing that would
// also pass for a create with nothing changed or removed.
it('names changed and removed counts in the notice, not just added, when updating an existing diagram', async () => {
  setup(() => Promise.resolve(json({
    document: generated, warnings: [], addedNodeIds: ['n_generated'], addedEdgeIds: [], changedElementCount: 2, removedElementCount: 1, provenance: { provider: 'ollama', model: 'llama3.1' },
  })));
  typePrompt('group the existing nodes into containers');
  fireEvent.click(screen.getByRole('button', { name: 'Generate' }));

  const notice = await waitFor(() => screen.getByText(/added/));
  expect(notice.textContent).toContain('1 added');
  expect(notice.textContent).toContain('2 changed');
  expect(notice.textContent).toContain('1 removed');
});

it('shows a working state while the request is in flight and clears it after', async () => {
  let finish: (response: Response) => void = () => undefined;
  const { onApply } = setup(() => new Promise((resolve) => { finish = resolve; }));
  typePrompt('a login flow');
  const button = screen.getByRole<HTMLButtonElement>('button', { name: /Generate/ });
  fireEvent.click(button);

  await waitFor(() => { expect(screen.getByRole('status').textContent).toBe('Generating…'); });
  expect(button.disabled).toBe(true);

  finish(json({ document: generated, warnings: [], addedNodeIds: ['n_generated'], addedEdgeIds: [], changedElementCount: 0, removedElementCount: 0, provenance: { provider: 'ollama', model: 'llama3.1' } }));
  await waitFor(() => { expect(onApply).toHaveBeenCalled(); });
  expect(screen.queryByText('Generating…')).toBeNull();
});

it('shows the model error plainly and applies nothing when the model is unreachable', async () => {
  const { onApply } = setup(() => Promise.resolve(json({ title: 'The AI provider is unreachable', detail: 'Could not reach Ollama at http://localhost:11434.' }, 502)));
  typePrompt('a login flow');
  fireEvent.click(screen.getByRole('button', { name: 'Generate' }));

  expect((await screen.findByRole('alert')).textContent).toBe('Could not reach Ollama at http://localhost:11434.');
  expect(onApply).not.toHaveBeenCalled();
});

it('shows a plain error when the generated output could not be parsed', async () => {
  const { onApply } = setup(() => Promise.resolve(json({ title: 'The model\'s output could not be parsed', detail: 'line 2, column 1: unexpected token' }, 422)));
  typePrompt('a login flow');
  fireEvent.click(screen.getByRole('button', { name: 'Generate' }));

  expect((await screen.findByRole('alert')).textContent).toBe('line 2, column 1: unexpected token');
  expect(onApply).not.toHaveBeenCalled();
});

// Two-sided with the plain-error test above: a *successful* second attempt after a failure must still go through -
// an error must not leave the panel stuck refusing further Generate calls.
it('lets Generate be retried after a failure', async () => {
  let attempt = 0;
  const { onApply } = setup(() => {
    attempt += 1;
    return Promise.resolve(attempt === 1
      ? json({ title: 'boom' }, 502)
      : json({ document: generated, warnings: [], addedNodeIds: ['n_generated'], addedEdgeIds: [], changedElementCount: 0, removedElementCount: 0, provenance: { provider: 'ollama', model: 'llama3.1' } }));
  });
  typePrompt('a login flow');
  fireEvent.click(screen.getByRole('button', { name: 'Generate' }));
  await screen.findByRole('alert');

  fireEvent.click(screen.getByRole('button', { name: 'Generate' }));

  await waitFor(() => { expect(onApply).toHaveBeenCalledOnce(); });
});

it('clears focus to the canvas on Escape and generates on Cmd/Ctrl+Enter', async () => {
  const { onEscape, onApply } = setup();
  typePrompt('a login flow');
  fireEvent.keyDown(screen.getByRole('textbox'), { key: 'Enter', metaKey: true });
  await waitFor(() => { expect(onApply).toHaveBeenCalled(); });

  fireEvent.keyDown(screen.getByRole('textbox'), { key: 'Escape' });
  expect(onEscape).toHaveBeenCalledOnce();
});

function setupDocument(responder: () => Promise<Response>, onRevealNode = vi.fn()) {
  const fetchMock = vi.fn<typeof fetch>(responder);
  vi.stubGlobal('fetch', fetchMock);
  render(<AiPanel document={original} active onApply={vi.fn()} onRevealNode={onRevealNode} onEscape={vi.fn()} />);
  fireEvent.click(screen.getByRole('tab', { name: 'To document' }));
  return { fetchMock, onRevealNode };
}

function documentResult(markdown: string): Response {
  return json({ markdown, provenance: { provider: 'ollama', model: 'llama3.1' } });
}

it('generates a document from the current diagram and renders the model markdown', async () => {
  const { fetchMock } = setupDocument(() => Promise.resolve(documentResult('The web tier [n_web] calls the API [n_api].')));

  fireEvent.click(screen.getByRole('button', { name: 'Generate' }));

  await screen.findByRole('button', { name: 'Web app' });
  // The citation reads as the node's label, never the id the model wrote it with.
  expect(screen.queryByText('n_web')).toBeNull();
  const [url, init] = fetchMock.mock.calls[0] ?? [];
  expect(url).toBe('/api/ai/diagram-to-document');
  expect(JSON.parse(init?.body as string)).toEqual({ documentId: original.id, outputKind: 'design-doc' });
});

// Two-sided with the default above: choosing a different output must actually reach the request, or the select is
// decorative.
it('sends the chosen output kind', async () => {
  const { fetchMock } = setupDocument(() => Promise.resolve(documentResult('# Runbook')));

  fireEvent.change(screen.getByLabelText('Output'), { target: { value: 'runbook' } });
  fireEvent.click(screen.getByRole('button', { name: 'Generate' }));

  await waitFor(() => { expect(fetchMock).toHaveBeenCalledOnce(); });
  const [, init] = fetchMock.mock.calls[0] ?? [];
  expect(JSON.parse(init?.body as string)).toEqual({ documentId: original.id, outputKind: 'runbook' });
});

it('selects and reveals the node when a cited id is clicked', async () => {
  const { onRevealNode } = setupDocument(() => Promise.resolve(documentResult('The web tier [n_web] calls the API [n_api].')));

  fireEvent.click(screen.getByRole('button', { name: 'Generate' }));
  fireEvent.click(await screen.findByRole('button', { name: 'Orders API' }));

  expect(onRevealNode).toHaveBeenCalledWith('n_api');
});

it('shows the model error plainly when the provider is unreachable', async () => {
  setupDocument(() => Promise.resolve(json({ title: 'The AI provider is unreachable', detail: 'Could not reach Ollama at http://localhost:11434.' }, 502)));

  fireEvent.click(screen.getByRole('button', { name: 'Generate' }));

  expect((await screen.findByRole('alert')).textContent).toBe('Could not reach Ollama at http://localhost:11434.');
});

it('copies the generated markdown to the clipboard', async () => {
  const writeText = vi.fn<(text: string) => Promise<void>>(() => Promise.resolve());
  Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true });
  setupDocument(() => Promise.resolve(documentResult('# Design\n\nKey node [n_web].')));

  fireEvent.click(screen.getByRole('button', { name: 'Generate' }));
  fireEvent.click(await screen.findByRole('button', { name: 'Copy' }));

  await waitFor(() => { expect(writeText).toHaveBeenCalledWith('# Design\n\nKey node [n_web].'); });
  delete (navigator as { clipboard?: unknown }).clipboard;
});

it('starts a sequence document on the sequence type, and offers it as a chip', async () => {
  const fetchMock = vi.fn<typeof fetch>(() => Promise.resolve(json({ document: original, warnings: [], addedNodeIds: [], addedEdgeIds: [], changedElementCount: 0, removedElementCount: 0, provenance: { provider: 'ollama', model: 'llama3.1' } })));
  vi.stubGlobal('fetch', fetchMock);
  render(<AiPanel document={checkoutSequence()} active onApply={vi.fn()} onEscape={vi.fn()} />);
  expect(screen.getByRole<HTMLInputElement>('radio', { name: 'Sequence' }).checked).toBe(true);
  expect(screen.getByRole<HTMLInputElement>('radio', { name: 'Flowchart' }).checked).toBe(false);
  typePrompt('a login flow');
  fireEvent.click(screen.getByRole('button', { name: 'Generate' }));
  await waitFor(() => { expect(fetchMock).toHaveBeenCalled(); });
  const [, init] = fetchMock.mock.calls[0] ?? [];
  expect((JSON.parse(init?.body as string) as { diagramType: string }).diagramType).toBe('sequence');
});
