import { useEffect, useState } from 'react';
import { consumePendingAiPanel, consumePendingTitleEdit } from '../app/newDocument.ts';
import { readDocumentVersion } from '../model/documentVersion.ts';
import { parseDocument } from '../model/parse.ts';
import type { Document } from '../model/types.ts';
import { AUTOSAVE_DELAY_MS, LIVE_SYNC_INTERVAL_MS } from '../editor/useDocumentEditor.ts';
import { fetchPublicThemes, type PublicThemes } from '../model/themesClient.ts';
import { DocumentViewer } from './DocumentViewer.tsx';

type LoadState = { kind: 'loading' } | { kind: 'ok'; document: Document; etag: string | undefined } | { kind: 'missing' } | { kind: 'error'; message: string };

interface Loaded {
  id: string;
  state: LoadState;
}

/** Fetches GET /api/documents/:id and renders it. The endpoint arrives with the storage lane; until then every id 404s. */
export function DocumentPage({ id }: { id: string }) {
  const [loaded, setLoaded] = useState<Loaded | undefined>(undefined);
  // A result for a different id is stale: show loading rather than the previous document.
  const state: LoadState = loaded?.id === id ? loaded.state : { kind: 'loading' };

  useEffect(() => {
    const controller = new AbortController();
    const setState = (next: LoadState): void => {
      setLoaded({ id, state: next });
    };
    fetch(`/api/documents/${encodeURIComponent(id)}`, { signal: controller.signal, headers: { Accept: 'application/json' } })
      .then(async (response) => {
        if (response.status === 404) {
          setState({ kind: 'missing' });
          return;
        }
        if (!response.ok) {
          throw new Error(`Server returned ${String(response.status)}`);
        }
        const etag = readDocumentVersion(response);
        const body: unknown = await response.json();
        setState({ kind: 'ok', document: parseDocument(body), etag });
      })
      .catch((error: unknown) => {
        if (controller.signal.aborted) {
          return;
        }
        setState({ kind: 'error', message: error instanceof Error ? error.message : 'Unknown error' });
      });
    return () => {
      controller.abort();
    };
  }, [id]);

  // The organisation's themes and default, for the viewer's Theme menu. Best effort: the editor works without them.
  const [orgThemes, setOrgThemes] = useState<PublicThemes | undefined>(undefined);
  useEffect(() => {
    let cancelled = false;
    void fetchPublicThemes()
      .then((themes) => {
        if (!cancelled) {
          setOrgThemes(themes);
        }
      })
      .catch(() => undefined);
    return () => {
      cancelled = true;
    };
  }, []);

  switch (state.kind) {
    case 'loading':
      return <p className="page-message">Loading document…</p>;
    case 'missing':
      return (
        <p className="page-message" role="alert">
          No document with id {id}.
        </p>
      );
    case 'error':
      return (
        <p className="page-message" role="alert">
          Could not load the document: {state.message}
        </p>
      );
    case 'ok':
      return (
        <DocumentViewer
          key={id}
          id={id}
          document={state.document}
          etag={state.etag}
          liveSyncIntervalMs={LIVE_SYNC_INTERVAL_MS}
          autosaveDelayMs={AUTOSAVE_DELAY_MS}
          initialTitleEditActive={consumePendingTitleEdit(id)}
          initialPanel={consumePendingAiPanel(id) ? 'ai' : undefined}
          orgThemes={orgThemes}
        />
      );
  }
}
