// @vitest-environment jsdom
import { forgetResetToken, takeResetToken, tokenInFragment } from './resetToken.ts';

function openAt(path: string): void {
  window.history.replaceState({ draughtsmanIndex: 3 }, '', path);
}

describe('the reset secret in the address bar', () => {
  afterEach(() => {
    forgetResetToken();
    openAt('/');
  });

  it('is read from the fragment only', () => {
    expect(tokenInFragment('#token=dpr_abc')).toBe('dpr_abc');
    expect(tokenInFragment('token=dpr_abc')).toBe('dpr_abc');
    expect(tokenInFragment('#other=1&token=dpr_abc')).toBe('dpr_abc');
    expect(tokenInFragment('')).toBeUndefined();
    expect(tokenInFragment('#token=')).toBeUndefined();
    expect(tokenInFragment('#t=dpr_abc')).toBeUndefined();
  });

  it('is taken out of the address bar and history entry at once, keeping the path, the query and the router state', () => {
    openAt('/reset-password?from=mail#token=dpr_abc');

    const token = takeResetToken();

    expect(token).toBe('dpr_abc');
    expect(window.location.hash).toBe('');
    expect(window.location.pathname + window.location.search).toBe('/reset-password?from=mail');
    expect(window.history.state).toEqual({ draughtsmanIndex: 3 });
  });

  it('is still there for the second mount React makes in development, and for nothing else once forgotten', () => {
    openAt('/reset-password#token=dpr_abc');

    expect(takeResetToken()).toBe('dpr_abc');
    expect(takeResetToken()).toBe('dpr_abc');

    forgetResetToken();
    expect(takeResetToken()).toBeUndefined();
  });

  it('is replaced by a newer link opened in the same tab', () => {
    openAt('/reset-password#token=dpr_first');
    takeResetToken();
    openAt('/reset-password#token=dpr_second');

    expect(takeResetToken()).toBe('dpr_second');
  });

  it('finds nothing at an address with no fragment', () => {
    openAt('/reset-password');

    expect(takeResetToken()).toBeUndefined();
  });
});
