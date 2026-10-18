import { assetIdOf, assetUrl, createImageNode, fitImageSize } from './assets.ts';
import type { Document } from './types.ts';

const document: Document = {
  id: 'doc_img',
  schemaVersion: 1,
  title: 'Images',
  type: 'architecture',
  nodes: [],
  edges: [],
  meta: { createdAt: '2026-09-28T00:00:00.000Z', updatedAt: '2026-09-28T00:00:00.000Z', createdBy: 't' },
};

describe('fitImageSize', () => {
  it('caps the long side at 240 and keeps the aspect ratio, whichever side is longer', () => {
    expect(fitImageSize(480, 240)).toEqual({ w: 240, h: 120 });
    expect(fitImageSize(240, 480)).toEqual({ w: 120, h: 240 });
    expect(fitImageSize(960, 640)).toEqual({ w: 240, h: 160 });
  });

  it('does not enlarge an image already within the cap', () => {
    expect(fitImageSize(100, 50)).toEqual({ w: 100, h: 50 });
  });

  it('falls back to a square for an image with no intrinsic size', () => {
    expect(fitImageSize(0, 0)).toEqual({ w: 240, h: 240 });
  });
});

describe('assetIdOf', () => {
  it('reads a non-empty string prop and ignores anything else', () => {
    expect(assetIdOf({ props: { assetId: 'asset_1' } })).toBe('asset_1');
    expect(assetIdOf({ props: { assetId: '' } })).toBeUndefined();
    expect(assetIdOf({ props: { assetId: 5 } })).toBeUndefined();
    expect(assetIdOf({})).toBeUndefined();
  });

  it('builds the served URL from an id', () => {
    expect(assetUrl('asset_1')).toBe('/api/assets/asset_1');
    expect(assetUrl('asset/a b')).toBe('/api/assets/asset%2Fa%20b');
  });
});

describe('createImageNode', () => {
  it('places a picture node centred on the point with its own size and stores the asset id', () => {
    const node = createImageNode(document, { x: 100, y: 80 }, 20, 'asset_1', { w: 240, h: 120 }, 'logo.png');

    expect(node.kind).toBe('image');
    expect(node.shape).toBe('image');
    expect(node.geometry).toEqual({ x: -20, y: 20, w: 240, h: 120 });
    expect(node.props).toEqual({ assetId: 'asset_1', fileName: 'logo.png' });
    expect(node.id.length).toBeGreaterThan(0);
  });
});
