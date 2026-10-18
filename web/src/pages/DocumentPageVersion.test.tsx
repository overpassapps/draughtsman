// @vitest-environment jsdom
import { act, render } from '@testing-library/react';
import orderService from '../../../docs/examples/order-service.json';
import { DocumentPage } from './DocumentPage.tsx';

// The editor's base version is whatever DocumentPage hands the viewer as `etag`; the viewer itself is not what is under test.
const seen: (string | undefined)[] = [];
vi.mock('./DocumentViewer.tsx', () => ({
  DocumentViewer: ({ etag }: { etag?: string | undefined }) => {
    seen.push(etag);
    return null;
  },
}));

function open(headers: Record<string, string>): Promise<void> {
  vi.stubGlobal('fetch', vi.fn<typeof fetch>(() => Promise.resolve(new Response(JSON.stringify(orderService), { status: 200, headers }))));
  render(<DocumentPage id={orderService.id} />);
  return act(() => Promise.resolve());
}

describe('DocumentPage base version', () => {
  beforeEach(() => {
    seen.length = 0;
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('starts the editor from X-Document-Version when a gateway has weakened the ETag', async () => {
    await open({ ETag: 'W/"dv1:gateway-rewrote-this"', 'X-Document-Version': 'dv1:loaded' });
    expect(seen.at(-1)).toBe('"dv1:loaded"');
  });

  it('starts the editor from the ETag, as a strong tag, when the server sends no X-Document-Version', async () => {
    await open({ ETag: 'W/"dv1:loaded"' });
    expect(seen.at(-1)).toBe('"dv1:loaded"');
  });
});
