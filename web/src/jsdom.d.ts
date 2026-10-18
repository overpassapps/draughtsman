/**
 * The little of jsdom the flythrough viewer's tests use: a fresh window per test (the standalone viewer binds window
 * listeners, which one shared window would accumulate). jsdom ships no types and @types/jsdom is not a dependency.
 */
declare module 'jsdom' {
  export class JSDOM {
    constructor(
      html?: string,
      options?: {
        runScripts?: 'dangerously' | 'outside-only';
        pretendToBeVisual?: boolean;
        url?: string;
        beforeParse?: (window: Window & typeof globalThis) => void;
      },
    );
    readonly window: Window & typeof globalThis;
  }
}
