// @vitest-environment jsdom
import { requestLayout, unpinOrthogonalRoutes } from './layoutClient.ts';
import { parseDocument } from './parse.ts';
import orderService from '../../../docs/examples/order-service.json';

describe('requestLayout', () => {
  afterEach(() => {
    globalThis.document.cookie = 'XSRF-TOKEN=; expires=Thu, 01 Jan 1970 00:00:00 GMT; path=/';
  });

  it('echoes the XSRF cookie, which the server requires of every cookie-authenticated POST', async () => {
    globalThis.document.cookie = 'XSRF-TOKEN=abc123; path=/';
    const document = parseDocument(orderService);
    const fetchImpl = vi.fn<typeof fetch>(() => Promise.resolve(new Response(JSON.stringify({ document, warnings: [] }), { status: 200 })));

    await requestLayout(document, 'layered', fetchImpl);

    const init = fetchImpl.mock.calls[0]?.[1];
    expect((init?.headers as Record<string, string>)['X-XSRF-TOKEN']).toBe('abc123');
  });
});

describe('unpinOrthogonalRoutes (S7)', () => {
  const doc = parseDocument(orderService);
  const bent = { x: 120, y: 80 };
  const withRouting = (routing: { mode: 'orthogonal' | 'straight' | 'curved'; waypoints?: { x: number; y: number }[] }) =>
    ({ ...doc, edges: doc.edges.map((edge, i) => (i === 0 ? { ...edge, routing } : edge)) });

  it('drops the bend points ELK wrote on an orthogonal connector so the canvas router draws it, and keeps its mode', () => {
    const out = unpinOrthogonalRoutes(withRouting({ mode: 'orthogonal', waypoints: [bent] }));
    expect(out.edges[0]?.routing).toEqual({ mode: 'orthogonal' });
    expect(out.edges[0]?.routing).not.toHaveProperty('waypoints');
  });

  it('leaves a curved or straight connector\'s points and a document with nothing to unpin untouched', () => {
    const curved = withRouting({ mode: 'curved', waypoints: [bent] });
    expect(unpinOrthogonalRoutes(curved).edges[0]?.routing?.waypoints).toEqual([bent]);
    const bare = { ...doc, edges: doc.edges.map((edge) => ({ ...edge, routing: { mode: 'orthogonal' as const } })) };
    expect(unpinOrthogonalRoutes(bare)).toBe(bare);
    const straight = withRouting({ mode: 'straight', waypoints: [bent] });
    expect(unpinOrthogonalRoutes(straight).edges[0]?.routing?.waypoints).toEqual([bent]);
  });

  it('is applied to a requested layout, so Tidy leaves connectors derived', async () => {
    const laid = withRouting({ mode: 'orthogonal', waypoints: [bent] });
    const fetchImpl = vi.fn<typeof fetch>(() => Promise.resolve(new Response(JSON.stringify({ document: laid, warnings: [] }), { status: 200 })));
    const { document } = await requestLayout(doc, 'layered', fetchImpl);
    expect(document.edges[0]?.routing).toEqual({ mode: 'orthogonal' });
  });
});
