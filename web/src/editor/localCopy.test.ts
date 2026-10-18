// @vitest-environment jsdom
import type { Document } from '../model/types.ts';
import { clearLocalCopy, MAX_COPIES, newCopyId, readLocalCopies, storeLocalCopy, type LocalCopy } from './localCopy.ts';

const document: Document = {
  id: 'doc_a',
  schemaVersion: 1,
  title: 'A',
  type: 'flowchart',
  nodes: [],
  edges: [],
  meta: { createdAt: '2026-09-30T00:00:00.000Z', updatedAt: '2026-09-30T00:00:00.000Z', createdBy: 't' },
};
const copy = (copyId: string, title = 'A', documentId = 'doc_a'): LocalCopy => ({ copyId, documentId, document: { ...document, title }, keptAt: '2026-09-30T10:00:00.000Z' });

describe('local copies', () => {
  beforeEach(() => {
    localStorage.clear();
  });
  afterEach(() => {
    vi.restoreAllMocks();
  });

  it('reads back what was stored, per document', () => {
    expect(readLocalCopies('doc_a')).toEqual([]);
    expect(storeLocalCopy(copy('c1', 'first'))).toBe(true);
    expect(storeLocalCopy(copy('c2', 'other doc', 'doc_b'))).toBe(true);
    expect(readLocalCopies('doc_a').map((c) => c.document.title)).toEqual(['first']);
    expect(readLocalCopies('doc_b').map((c) => c.document.title)).toEqual(['other doc']);
  });

  it('replaces a copy with the same id and keeps the others, so an earlier one survives a later refusal', () => {
    storeLocalCopy(copy('old', 'earlier visit'));
    storeLocalCopy(copy('mine', 'v1'));
    storeLocalCopy(copy('mine', 'v2'));
    expect(readLocalCopies('doc_a').map((c) => [c.copyId, c.document.title])).toEqual([['old', 'earlier visit'], ['mine', 'v2']]);
  });

  it('keeps at most MAX_COPIES, dropping the oldest', () => {
    for (let i = 0; i < MAX_COPIES + 2; i += 1) {
      storeLocalCopy(copy(`c${String(i)}`));
    }
    const ids = readLocalCopies('doc_a').map((c) => c.copyId);
    expect(ids).toHaveLength(MAX_COPIES);
    expect(ids[0]).toBe('c2');
    expect(ids.at(-1)).toBe(`c${String(MAX_COPIES + 1)}`);
  });

  it('clears one copy and leaves the rest', () => {
    storeLocalCopy(copy('a'));
    storeLocalCopy(copy('b'));
    clearLocalCopy('doc_a', 'a');
    expect(readLocalCopies('doc_a').map((c) => c.copyId)).toEqual(['b']);
    clearLocalCopy('doc_a', 'b');
    expect(localStorage.getItem('draughtsman.localCopy.doc_a')).toBeNull();
  });

  it('reads nothing from storage that is corrupt or belongs to another document, rather than throwing', () => {
    localStorage.setItem('draughtsman.localCopy.doc_a', '{not json');
    expect(readLocalCopies('doc_a')).toEqual([]);
    localStorage.setItem('draughtsman.localCopy.doc_a', JSON.stringify([copy('x', 'A', 'doc_other'), { nonsense: true }, copy('y')]));
    expect(readLocalCopies('doc_a').map((c) => c.copyId)).toEqual(['y']);
  });

  it('reports false, and does not throw, when the browser will not store it', () => {
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new DOMException('full', 'QuotaExceededError');
    });
    expect(storeLocalCopy(copy('c'))).toBe(false);
  });

  it('hands out a different id each time', () => {
    expect(newCopyId()).not.toBe(newCopyId());
  });
});
