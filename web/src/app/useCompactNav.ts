import { useEffect, useState } from 'react';

/** Below this width the shell's links fold into one Menu button (QA S3): the ten flat admin links need about 930 px. */
export const COMPACT_NAV_MAX_WIDTH = 930;

const QUERY = `(max-width: ${String(COMPACT_NAV_MAX_WIDTH)}px)`;

/**
 * True while the viewport is narrow enough for the compact shell. It is a hook rather than a pure CSS switch so the links
 * exist once in the DOM: a stylesheet that hides a second copy would still leave both in a test, and a hidden-but-focusable
 * duplicate is exactly what a keyboard user trips over. Without `matchMedia` (jsdom) the wide layout is assumed.
 */
export function useCompactNav(): boolean {
  const [compact, setCompact] = useState(() => (typeof window.matchMedia === 'function' ? window.matchMedia(QUERY).matches : false));
  useEffect(() => {
    if (typeof window.matchMedia !== 'function') {
      return;
    }
    const query = window.matchMedia(QUERY);
    const onChange = (): void => {
      setCompact(query.matches);
    };
    onChange();
    query.addEventListener('change', onChange);
    return () => {
      query.removeEventListener('change', onChange);
    };
  }, []);
  return compact;
}
