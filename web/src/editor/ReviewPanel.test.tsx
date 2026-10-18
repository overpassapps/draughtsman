// @vitest-environment jsdom
import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import type { CritiqueFinding } from '../model/historyClient.ts';
import type { Document } from '../model/types.ts';
import { ReviewPanel } from './ReviewPanel.tsx';
import { orderFindings, REVIEW_DEBOUNCE_MS, summaryLine } from './reviewFormat.ts';

const doc: Document = {
  id: 'doc_r',
  schemaVersion: 1,
  title: 'R',
  type: 'flowchart',
  nodes: [{ id: 'n_a', kind: 'process', label: 'Receive', shape: 'rectangle' }, { id: 'n_b', kind: 'process', label: '', shape: 'rectangle' }],
  edges: [{ id: 'e_1', kind: 'flow', from: { node: 'n_a' }, to: { node: 'n_b' } }],
  meta: { createdAt: '2026-10-01T00:00:00.000Z', updatedAt: '2026-10-01T00:00:00.000Z', createdBy: 't' },
};

const findings: CritiqueFinding[] = [
  { rule: 'isolated-node', severity: 'warning', message: 'Node is not connected.', nodeIds: ['n_a'], edgeIds: [] },
  { rule: 'dangling-edge', severity: 'error', message: 'An edge points at nothing.', nodeIds: [], edgeIds: ['e_1'] },
];

function serve(result: () => Response) {
  const fetchMock = vi.fn<typeof fetch>(() => Promise.resolve(result()));
  vi.stubGlobal('fetch', fetchMock);
  return fetchMock;
}

const ok = (body: CritiqueFinding[]) => () => new Response(JSON.stringify({ findings: body }), { status: 200 });

describe('ReviewPanel', () => {
  afterEach(() => {
    cleanup();
    vi.unstubAllGlobals();
    vi.useRealTimers();
  });

  it('runs when the tab opens, lists errors before warnings and says how many of each', async () => {
    const fetchMock = serve(ok(findings));
    render(<ReviewPanel document={doc} active onSelect={() => undefined} onEscape={() => undefined} />);

    expect((await screen.findByTestId('review-summary')).textContent).toBe('2 findings: 1 error, 1 warning');
    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect((JSON.parse(fetchMock.mock.calls[0]?.[1]?.body as string) as { id: string }).id).toBe('doc_r');
    const rows = within(screen.getByRole('list', { name: 'Findings' })).getAllByRole('listitem');
    expect(rows[0]?.textContent).toContain('dangling-edge');
    expect(rows[1]?.textContent).toContain('isolated-node');
  });

  it('says the structure checks passed when there is nothing to report', async () => {
    serve(ok([]));
    render(<ReviewPanel document={doc} active onSelect={() => undefined} onEscape={() => undefined} />);

    expect((await screen.findByTestId('review-summary')).textContent).toBe('No findings. The structure checks passed.');
    expect(screen.queryByRole('list', { name: 'Findings' })).toBeNull();
  });

  it('selects the elements a finding is about, from the row or from one of its chips, naming edges by their ends', async () => {
    serve(ok(findings));
    const onSelect = vi.fn<(ids: readonly string[]) => void>();
    render(<ReviewPanel document={doc} active onSelect={onSelect} onEscape={() => undefined} />);
    await screen.findByTestId('review-summary');

    fireEvent.click(screen.getByRole('button', { name: 'error: An edge points at nothing.' }));
    expect(onSelect).toHaveBeenLastCalledWith(['e_1']);
    // The edge chip reads "Receive → n_b": the second node has no label, so its id stands in.
    fireEvent.click(screen.getByRole('button', { name: 'Receive → n_b' }));
    expect(onSelect).toHaveBeenLastCalledWith(['e_1']);
    fireEvent.click(screen.getByRole('button', { name: 'Receive' }));
    expect(onSelect).toHaveBeenLastCalledWith(['n_a']);
  });

  it('runs nothing while the tab is closed, and runs again once the document has been still for a moment', async () => {
    vi.useFakeTimers();
    const fetchMock = serve(ok(findings));
    const { rerender } = render(<ReviewPanel document={doc} active={false} onSelect={() => undefined} onEscape={() => undefined} />);
    await act(async () => { await vi.advanceTimersByTimeAsync(5_000); });
    expect(fetchMock).not.toHaveBeenCalled();

    rerender(<ReviewPanel document={doc} active onSelect={() => undefined} onEscape={() => undefined} />);
    await act(async () => { await vi.advanceTimersByTimeAsync(0); });
    expect(fetchMock).toHaveBeenCalledTimes(1);

    // An edit re-runs it, but only after the pause, and several edits inside the pause make one request.
    const edited = { ...doc, title: 'R2' };
    rerender(<ReviewPanel document={edited} active onSelect={() => undefined} onEscape={() => undefined} />);
    rerender(<ReviewPanel document={{ ...edited, title: 'R3' }} active onSelect={() => undefined} onEscape={() => undefined} />);
    await act(async () => { await vi.advanceTimersByTimeAsync(REVIEW_DEBOUNCE_MS - 50); });
    expect(fetchMock).toHaveBeenCalledTimes(1);
    await act(async () => { await vi.advanceTimersByTimeAsync(100); });
    expect(fetchMock).toHaveBeenCalledTimes(2);
    expect((JSON.parse(fetchMock.mock.calls[1]?.[1]?.body as string) as { title: string }).title).toBe('R3');
  });

  it('keeps the last good result visible, marked as of its time, when a re-run fails, and can try again', async () => {
    let fail = false;
    serve(() => (fail ? new Response(JSON.stringify({ detail: 'The server is busy.' }), { status: 503 }) : new Response(JSON.stringify({ findings }), { status: 200 })));
    render(<ReviewPanel document={doc} active onSelect={() => undefined} onEscape={() => undefined} />);
    await screen.findByTestId('review-summary');

    fail = true;
    fireEvent.click(screen.getByRole('button', { name: 'Refresh' }));

    const alert = await screen.findByRole('alert');
    expect(alert.textContent).toContain('The server is busy.');
    expect(alert.textContent).toContain('Showing the result as of');
    expect(screen.getByTestId('review-summary').textContent).toBe('2 findings: 1 error, 1 warning');

    fail = false;
    fireEvent.click(within(alert).getByRole('button', { name: 'Try again' }));
    await waitFor(() => {
      expect(screen.queryByRole('alert')).toBeNull();
    });
  });

  it('orders and words findings the way the panel promises', () => {
    expect(orderFindings(findings).map((f) => f.severity)).toEqual(['error', 'warning']);
    expect(summaryLine(findings.slice(0, 1))).toBe('1 finding: 1 warning');
    expect(summaryLine([])).toContain('No findings');
  });
});
