import { SaveFailed } from '../model/documentClient.ts';
import { classifySaveFailure, MAX_RETRY_DELAY_MS, retryDelay } from './saveFailure.ts';

describe('classifySaveFailure', () => {
  it.each([401, 408, 429, 500, 502, 503])('retries a %i, which the same save can get past later', (status) => {
    expect(classifySaveFailure(new SaveFailed(status)).retry).toBe(true);
  });

  it.each([400, 403, 404, 413, 422])('does not retry a %i, which the same save would meet again', (status) => {
    expect(classifySaveFailure(new SaveFailed(status)).retry).toBe(false);
  });

  it('retries when the request never reached the server, which is how fetch reports being offline', () => {
    const failure = classifySaveFailure(new TypeError('Failed to fetch'));
    expect(failure.retry).toBe(true);
    expect(failure.message).toMatch(/cannot reach the server/i);
  });

  it('says what each case means for the user and that their edits are still there', () => {
    expect(classifySaveFailure(new SaveFailed(401)).message).toMatch(/signed out/i);
    expect(classifySaveFailure(new SaveFailed(403)).message).toMatch(/permission/i);
    expect(classifySaveFailure(new SaveFailed(404)).message).toMatch(/no longer exists/i);
    expect(classifySaveFailure(new SaveFailed(500)).message).toContain('500');
  });

  it('does not retry an error it does not recognise', () => {
    expect(classifySaveFailure(new Error('boom'))).toEqual({ retry: false, message: 'boom' });
    expect(classifySaveFailure('odd').retry).toBe(false);
  });
});

describe('retryDelay', () => {
  it('starts at the autosave delay and doubles with each failure', () => {
    expect([1, 2, 3, 4].map((failures) => retryDelay(failures, 2000))).toEqual([2000, 4000, 8000, 16_000]);
  });

  it('stops growing at the cap, so a server that comes back is noticed within half a minute', () => {
    expect(retryDelay(5, 2000)).toBe(MAX_RETRY_DELAY_MS);
    expect(retryDelay(50, 2000)).toBe(MAX_RETRY_DELAY_MS);
  });
});
