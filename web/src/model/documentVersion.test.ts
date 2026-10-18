import { DOCUMENT_VERSION_HEADER, readDocumentVersion } from './documentVersion.ts';

function responseWith(headers: Record<string, string>): Response {
  return new Response('{}', { status: 200, headers });
}

describe('readDocumentVersion', () => {
  it('names the same header the server sends', () => {
    expect(DOCUMENT_VERSION_HEADER).toBe('X-Document-Version');
  });

  it('reads X-Document-Version first and quotes it', () => {
    expect(readDocumentVersion(responseWith({ 'X-Document-Version': 'dv1:aaaa' }))).toBe('"dv1:aaaa"');
  });

  it('prefers X-Document-Version over an ETag a gateway has rewritten', () => {
    expect(readDocumentVersion(responseWith({ 'X-Document-Version': 'dv1:aaaa', ETag: 'W/"dv1:bbbb"' }))).toBe('"dv1:aaaa"');
  });

  it('falls back to the ETag, and turns a weak one back into the strong form', () => {
    expect(readDocumentVersion(responseWith({ ETag: '"dv1:aaaa"' }))).toBe('"dv1:aaaa"');
    expect(readDocumentVersion(responseWith({ ETag: 'W/"dv1:aaaa"' }))).toBe('"dv1:aaaa"');
  });

  it('does not double the quotes when something already quoted the header', () => {
    expect(readDocumentVersion(responseWith({ 'X-Document-Version': '"dv1:aaaa"' }))).toBe('"dv1:aaaa"');
  });

  it('is undefined when the response carries neither, or only an empty one', () => {
    expect(readDocumentVersion(responseWith({}))).toBeUndefined();
    expect(readDocumentVersion(responseWith({ 'X-Document-Version': ' ' }))).toBeUndefined();
  });
});
