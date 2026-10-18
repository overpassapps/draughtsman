/**
 * Whether an AI provider is configured on this instance, from the same `ai.enabled` the editor's AI tab gates on
 * (`GET /api/health`), asked only while `enabled` (the home screen asks once the list turns out to be empty, so a populated
 * home makes no extra request). Undefined until known, and false on any failure: a card or tab that offers AI is hidden, never broken.
 */
import { useEffect, useState } from 'react';

export function useAiAvailable(enabled = true): boolean | undefined {
  const [available, setAvailable] = useState<boolean>();
  useEffect(() => {
    if (!enabled) {
      return;
    }
    const controller = new AbortController();
    // Through a promise so a fetch that throws at once (no network layer) is the same as one that fails.
    void Promise.resolve()
      .then(() => fetch('/api/health', { signal: controller.signal, headers: { Accept: 'application/json' } }))
      .then(async (response) => {
        if (!response.ok) {
          setAvailable(false);
          return;
        }
        const body = (await response.json()) as { ai?: { enabled?: boolean } };
        setAvailable(body.ai?.enabled === true);
      })
      .catch(() => {
        // Aborted on unmount, or the server is unreachable: AI is simply not offered.
        if (!controller.signal.aborted) {
          setAvailable(false);
        }
      });
    return () => {
      controller.abort();
    };
  }, [enabled]);
  return available;
}
