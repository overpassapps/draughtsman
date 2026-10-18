/**
 * The home screen: the documents in the workspace, a way to make a new one, and the way into each (design/atlas/
 * document-list.md). A "New diagram" menu creates a blank diagram of the model's types and opens it; the list shows
 * each document's type, title and last-modified time, opens on click, and a kebab menu renames or deletes it, the
 * delete asking for confirmation that names the document first. Import SQL and Import Mermaid, beside New diagram, open the
 * paste boxes that draw a schema from CREATE TABLE statements (editor/ImportSqlDialog.tsx) and a flowchart or sequence
 * diagram from Mermaid (editor/ImportMermaidDialog.tsx). An empty instance shows the atlas's empty state (design/atlas/
 * empty-loading-error-states.md): a heading, one line, Flowchart / Architecture / From text cards (From text only while an
 * AI provider is configured), the two importers, and an opt-in "Add four sample diagrams" (app/samples.ts).
 * Search (by title, server-side), sort (the Title, Type and Last modified headers) and a type filter narrow the list; the
 * kebab menu also duplicates. Delete moves a document to the bin with an Undo toast rather than asking first, because it is
 * recoverable; the Bin view (BinView.tsx) restores it or, as a separate confirmed step, deletes it for good
 * (design/atlas/document-list.md, OP-02 and PR-06). The documents show as a grid of cards, each with the server's thumbnail of the
 * diagram (DocumentCards.tsx, DocumentThumbnail.tsx), or as the table; the choice is remembered per browser (app/homeLayout.ts). Folders
 * and starring are still later.
 */

import { useCallback, useEffect, useRef, useState, type KeyboardEvent, type ReactNode } from 'react';
import { navigate } from '../app/router.ts';
import { ImportDrawioDialog } from '../editor/ImportDrawioDialog.tsx';
import { ImportMermaidDialog } from '../editor/ImportMermaidDialog.tsx';
import { ImportSqlDialog } from '../editor/ImportSqlDialog.tsx';
import { ImportVisioDialog } from '../editor/ImportVisioDialog.tsx';
import { SEARCH_DEBOUNCE_MS, STATUS_MS, UNDO_TOAST_MS } from '../app/homeList.ts';
import { loadHomeLayout, saveHomeLayout, type HomeLayout } from '../app/homeLayout.ts';
import { DIAGRAM_TEMPLATES, type DiagramTemplate } from '../app/diagramTemplates.ts';
import { DIAGRAM_TYPES, createAndOpen } from '../app/newDocument.ts';
import { createSamples } from '../app/samples.ts';
import { useAiAvailable } from '../app/useAiAvailable.ts';
import { restoreDocument } from '../model/binClient.ts';
import { deleteDocument, duplicateDocument, fetchDocumentEtag, listDocumentsPage, renameDocument, type DocumentListQuery, type DocumentSort, type DocumentSummary } from '../model/documentClient.ts';
import type { DocumentType } from '../model/types.ts';
import { dateTimeTitle, formatDate } from '../model/dateFormat.ts';
import { BinView } from './BinView.tsx';
import { DocumentCards } from './DocumentCards.tsx';

type ListState = { kind: 'loading' } | { kind: 'ok'; documents: DocumentSummary[] } | { kind: 'error'; message: string };

/** Lucide icons (ISC licence) for the four types: workflow, boxes, shapes and arrow-left-right. */
const TYPE_ICONS: Record<DocumentType, ReactNode> = {
  flowchart: (
    <>
      <rect width="8" height="8" x="3" y="3" rx="2" />
      <rect width="8" height="8" x="13" y="3" rx="2" />
      <rect width="8" height="8" x="8" y="13" rx="2" />
      <path d="M7 11v1a1 1 0 0 0 1 1h2" />
      <path d="M13 11h2a1 1 0 0 1 1 1v1" />
    </>
  ),
  architecture: (
    <>
      <path d="M3 3h18v18H3z" />
      <path d="M7 7h4v4H7z" />
      <path d="M13 13h4v4h-4z" />
      <path d="M11 9h2" />
      <path d="M9 11v2" />
    </>
  ),
  freeform: (
    <>
      <circle cx="8" cy="8" r="3" />
      <rect x="14" y="5" width="7" height="7" rx="1" />
      <path d="m5 17 4-5 3 3 3-4 4 6z" />
    </>
  ),
  sequence: (
    <>
      <path d="M8 3 4 7l4 4" />
      <path d="M4 7h16" />
      <path d="m16 21 4-4-4-4" />
      <path d="M20 17H4" />
    </>
  ),
};

function TypeIcon({ type }: { type: DocumentType }) {
  return (
    <>
      <svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
        {TYPE_ICONS[type]}
      </svg>
      <span className="visually-hidden">{typeLabel(type)}</span>
    </>
  );
}

/** The direction each column starts in when first chosen: newest first for times, A to Z for words. */
const NATURAL_DIRECTION: Record<DocumentSort, 'asc' | 'desc'> = { modified: 'desc', created: 'desc', title: 'asc', type: 'asc' };

/** What the grid's Sort by menu offers: the table's sortable columns, each way round. */
const SORT_CHOICES: { value: string; label: string }[] = [
  { value: 'modified:desc', label: 'Last modified, newest first' },
  { value: 'modified:asc', label: 'Last modified, oldest first' },
  { value: 'title:asc', label: 'Title, A to Z' },
  { value: 'title:desc', label: 'Title, Z to A' },
  { value: 'type:asc', label: 'Type, A to Z' },
  { value: 'type:desc', label: 'Type, Z to A' },
];

function typeLabel(type: DocumentType): string {
  return DIAGRAM_TYPES.find((option) => option.type === type)?.label ?? type;
}

/** A relative "last modified" time, with the absolute time on hover. */
function relativeTime(iso: string): { relative: string; absolute: string } {
  const then = Date.parse(iso);
  const seconds = Math.max(0, Math.round((Date.now() - then) / 1000));
  const absolute = dateTimeTitle(then);
  if (seconds < 60) {
    return { relative: 'just now', absolute };
  }
  const minutes = Math.round(seconds / 60);
  if (minutes < 60) {
    return { relative: `${String(minutes)}m ago`, absolute };
  }
  const hours = Math.round(minutes / 60);
  if (hours < 24) {
    return { relative: `${String(hours)}h ago`, absolute };
  }
  const days = Math.round(hours / 24);
  if (days < 7) {
    return { relative: `${String(days)}d ago`, absolute };
  }
  return { relative: formatDate(then), absolute };
}

const MORE_ICON = (
  <>
    <circle cx="5" cy="12" r="1" />
    <circle cx="12" cy="12" r="1" />
    <circle cx="19" cy="12" r="1" />
  </>
);

export function HomePage() {
  const [state, setState] = useState<ListState>({ kind: 'loading' });
  const [menuOpen, setMenuOpen] = useState(false);
  const [busy, setBusy] = useState(false);
  const [importOpen, setImportOpen] = useState(false);
  const [mermaidOpen, setMermaidOpen] = useState(false);
  const [drawioOpen, setDrawioOpen] = useState(false);
  const [visioOpen, setVisioOpen] = useState(false);
  const [error, setError] = useState<string | undefined>(undefined);
  // Which document's kebab is open, and which is being renamed in place.
  const [kebabFor, setKebabFor] = useState<string | undefined>(undefined);
  const [renameFor, setRenameFor] = useState<string | undefined>(undefined);
  const [renameValue, setRenameValue] = useState('');
  const [view, setView] = useState<'documents' | 'bin'>('documents');
  // Grid of cards with thumbnails, or the table: remembered for this browser, the grid until chosen otherwise.
  const [layout, setLayout] = useState<HomeLayout>(loadHomeLayout);
  // What narrows and orders the list. `search` is what is typed; `query` is what the list was last asked for.
  const [search, setSearch] = useState('');
  const [query, setQuery] = useState('');
  const [sort, setSort] = useState<{ field: DocumentSort; dir: 'asc' | 'desc' }>({ field: 'modified', dir: 'desc' });
  const [typeFilter, setTypeFilter] = useState<DocumentType | ''>('');
  // "Moved to the bin" with its Undo, and the live-region text that announces the last action.
  // The one place an action's outcome is said (QA P23): a visible line that is also the live region a screen reader
  // hears, so nothing is announced twice and a sighted person sees what a screen-reader user is told. `undoId` is set
  // only for a delete, which offers Undo; every other message just goes by itself.
  const [toast, setToast] = useState<{ message: string; undoId?: string } | undefined>(undefined);
  const say = useCallback((message: string, undoId?: string): void => {
    setToast(undoId === undefined ? { message } : { message, undoId });
  }, []);
  const newMenu = useRef<HTMLButtonElement>(null);
  const searchField = useRef<HTMLInputElement>(null);
  // The latest request wins: a slow answer to an earlier search must not replace a later one.
  const requests = useRef(0);

  const reload = useCallback((): Promise<void> => {
    const mine = ++requests.current;
    const listQuery: DocumentListQuery = {
      q: query,
      ...(sort.field === 'modified' && sort.dir === 'desc' ? {} : { sort: sort.field, dir: sort.dir }),
      ...(typeFilter === '' ? {} : { type: typeFilter }),
    };
    return listDocumentsPage(listQuery)
      .then((page) => {
        if (mine === requests.current) {
          setState({ kind: 'ok', documents: page.items });
          setBusy(false);
        }
      })
      .catch((cause: unknown) => {
        if (mine === requests.current) {
          setBusy(false);
          setState({ kind: 'error', message: cause instanceof Error ? cause.message : 'Unknown error' });
        }
      });
  }, [query, sort, typeFilter]);

  useEffect(() => {
    void reload();
  }, [reload]);

  // Typing searches after a pause, not on every keystroke.
  useEffect(() => {
    if (search.trim() === query) {
      return undefined;
    }
    const timer = setTimeout(() => { setQuery(search.trim()); }, SEARCH_DEBOUNCE_MS);
    return () => { clearTimeout(timer); };
  }, [search, query]);

  // "/" jumps to the search field, as in the other lists the atlas cites, when nothing that takes text has focus.
  useEffect(() => {
    const onKey = (e: globalThis.KeyboardEvent): void => {
      const target = e.target instanceof HTMLElement ? e.target : undefined;
      const typing = target !== undefined && (target.isContentEditable || ['INPUT', 'TEXTAREA', 'SELECT'].includes(target.tagName));
      if (e.key === '/' && !typing && !e.metaKey && !e.ctrlKey && !e.altKey && view === 'documents') {
        e.preventDefault();
        searchField.current?.focus();
      }
    };
    window.addEventListener('keydown', onKey);
    return () => { window.removeEventListener('keydown', onKey); };
  }, [view]);

  // The Undo toast goes by itself.
  useEffect(() => {
    if (toast === undefined) {
      return undefined;
    }
    const timer = setTimeout(() => { setToast(undefined); }, toast.undoId === undefined ? STATUS_MS : UNDO_TOAST_MS);
    return () => { clearTimeout(timer); };
  }, [toast]);

  const filtering = query !== '' || typeFilter !== '';

  const documents = state.kind === 'ok' ? state.documents : [];
  // Only the empty state offers "From text", so only an empty list asks whether AI is configured.
  const aiAvailable = useAiAvailable(state.kind === 'ok' && documents.length === 0);

  const createFromTemplate = (template: DiagramTemplate): void => {
    setMenuOpen(false);
    setBusy(true);
    setError(undefined);
    void createAndOpen(template.type, { template: template.id })
      .catch((cause: unknown) => {
        setError(cause instanceof Error ? cause.message : 'Unknown error');
        setBusy(false);
      });
  };

  const create = (type: DocumentType): void => {
    setMenuOpen(false);
    setBusy(true);
    setError(undefined);
    void createAndOpen(type)
      .catch((cause: unknown) => {
        setError(cause instanceof Error ? cause.message : 'Unknown error');
        setBusy(false);
      });
  };

  // Opt-in samples (PR-13): pressed, never automatic. They arrive in the list in place of the empty state.
  const addSamples = (): void => {
    setBusy(true);
    setError(undefined);
    void createSamples()
      .then(() => reload())
      .catch((cause: unknown) => {
        setError(cause instanceof Error ? cause.message : 'Unknown error');
        // Some may have been created before one failed; show whatever exists rather than leave a stale empty state.
        void reload();
      });
  };

  const open = (id: string): void => {
    navigate(`/d/${encodeURIComponent(id)}`);
  };

  const beginRename = (document: DocumentSummary): void => {
    setKebabFor(undefined);
    setRenameFor(document.id);
    setRenameValue(document.title);
  };

  const commitRename = (document: DocumentSummary): void => {
    const title = renameValue.trim();
    const finish = (): void => {
      setRenameFor(undefined);
    };
    if (title.length === 0 || title === document.title) {
      finish();
      return;
    }
    setBusy(true);
    setError(undefined);
    void fetchDocumentEtag(document.id)
      .then((etag) => renameDocument(document.id, title, etag))
      .then(() => {
        setRenameFor(undefined);
        return reload();
      })
      .catch((cause: unknown) => {
        setBusy(false);
        // A stale save (the document moved while it was open) surfaces as the list being out of date; reload it.
        if (cause instanceof Error && cause.name === 'SaveConflict') {
          void reload();
          return;
        }
        setError(cause instanceof Error ? cause.message : 'Unknown error');
      });
  };

  // Delete is recoverable, so it does not ask first: the document goes to the bin and the toast offers Undo.
  const moveToBin = (document: DocumentSummary): void => {
    setKebabFor(undefined);
    setBusy(true);
    setError(undefined);
    void deleteDocument(document.id)
      .then(() => {
        say(`Moved “${document.title}” to the bin.`, document.id);
        return reload();
      })
      .catch((cause: unknown) => {
        setBusy(false);
        setError(cause instanceof Error ? cause.message : 'Unknown error');
      });
  };

  const undoDelete = (): void => {
    if (toast?.undoId === undefined) {
      return;
    }
    const id = toast.undoId;
    const title = /^Moved “(.*)” to the bin\.$/.exec(toast.message)?.[1] ?? 'The document';
    setToast(undefined);
    setBusy(true);
    setError(undefined);
    void restoreDocument(id)
      .then((restored) => {
        say(restored ? `Restored “${title}”.` : `“${title}” is no longer in the bin.`);
        return reload();
      })
      .catch((cause: unknown) => {
        setBusy(false);
        setError(cause instanceof Error ? cause.message : 'Unknown error');
      });
  };

  const duplicate = (document: DocumentSummary): void => {
    setKebabFor(undefined);
    setBusy(true);
    setError(undefined);
    void duplicateDocument(document.id)
      .then(() => {
        say(`Duplicated “${document.title}”.`);
        return reload();
      })
      .catch((cause: unknown) => {
        setBusy(false);
        setError(cause instanceof Error ? cause.message : 'Unknown error');
      });
  };

  const sortBy = (field: DocumentSort): void => {
    setSort((now) => (now.field === field ? { field, dir: now.dir === 'asc' ? 'desc' : 'asc' } : { field, dir: NATURAL_DIRECTION[field] }));
  };

  const onRenameKeyDown = (document: DocumentSummary) => (e: KeyboardEvent<HTMLInputElement>): void => {
    if (e.key === 'Enter') {
      commitRename(document);
    } else if (e.key === 'Escape') {
      setRenameFor(undefined);
    }
  };

  const ariaSort = (field: DocumentSort): 'ascending' | 'descending' | 'none' => (sort.field === field ? (sort.dir === 'asc' ? 'ascending' : 'descending') : 'none');
  const sortMark = (field: DocumentSort): string => (sort.field === field ? (sort.dir === 'asc' ? ' ↑' : ' ↓') : '');

  const chooseLayout = (next: HomeLayout): void => {
    setLayout(next);
    saveHomeLayout(next);
  };

  // What a document's title is, and what its kebab holds, are the same in the table and in a card.
  const titleControl = (document: DocumentSummary): ReactNode =>
    renameFor === document.id ? (
      <input
        className="home-rename-input"
        value={renameValue}
        autoFocus
        aria-label={`Rename ${document.title}`}
        onChange={(e) => { setRenameValue(e.target.value); }}
        onKeyDown={onRenameKeyDown(document)}
        onBlur={() => { commitRename(document); }}
      />
    ) : (
      <button type="button" className="home-open" onClick={() => { open(document.id); }}>
        {document.title}
      </button>
    );

  const actionsMenu = (document: DocumentSummary): ReactNode => (
    <div className="home-kebab">
      <button
        type="button"
        aria-haspopup="menu"
        aria-expanded={kebabFor === document.id}
        aria-label={`Actions for ${document.title}`}
        onClick={() => { setKebabFor((current) => (current === document.id ? undefined : document.id)); }}
      >
        <svg viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
          {MORE_ICON}
        </svg>
      </button>
      {kebabFor === document.id && (
        <div className="home-kebab-menu" role="menu" aria-label={`Actions for ${document.title}`}>
          <button type="button" role="menuitem" onClick={() => { beginRename(document); }}>
            Rename
          </button>
          <button type="button" role="menuitem" onClick={() => { duplicate(document); }}>
            Duplicate
          </button>
          <button type="button" role="menuitem" className="home-delete-item" onClick={() => { moveToBin(document); }}>
            Delete
          </button>
        </div>
      )}
    </div>
  );

  return (
    <div className="home">
      <header className="home-header">
        <h1 className="home-title">{view === 'bin' ? 'Bin' : 'Documents'}</h1>
        <div className="home-header-actions">
          <div className="home-views" role="group" aria-label="View">
            <button type="button" aria-pressed={view === 'documents'} onClick={() => { setView('documents'); }}>Documents</button>
            <button type="button" aria-pressed={view === 'bin'} onClick={() => { setView('bin'); }}>Bin</button>
          </div>
          <button type="button" className="home-import-button" onClick={() => { setImportOpen(true); }} disabled={busy}>
            Import SQL
          </button>
          <button type="button" className="home-import-button" onClick={() => { setMermaidOpen(true); }} disabled={busy}>
            Import Mermaid
          </button>
          <button type="button" className="home-import-button" onClick={() => { setDrawioOpen(true); }} disabled={busy}>
            Import draw.io
          </button>
          <button type="button" className="home-import-button" onClick={() => { setVisioOpen(true); }} disabled={busy}>
            Import Visio
          </button>
          <div className="home-new">
            <button ref={newMenu} type="button" className="home-new-button" aria-haspopup="menu" aria-expanded={menuOpen} onClick={() => { setMenuOpen((open) => !open); }} disabled={busy}>
              New diagram
            </button>
            {menuOpen && (
              <div className="home-new-menu" role="menu" aria-label="New diagram" onKeyDown={(e) => { if (e.key === 'Escape') { setMenuOpen(false); newMenu.current?.focus(); } }}>
                {DIAGRAM_TYPES.map((option) => (
                  <button
                    key={option.type}
                    type="button"
                    role="menuitem"
                    className="home-new-item"
                    onClick={() => { create(option.type); }}
                  >
                    {option.label}
                  </button>
                ))}
                <div className="home-new-more" role="group" aria-label="More diagram types">
                  <span className="home-new-more-title" aria-hidden="true">More diagram types</span>
                  {DIAGRAM_TEMPLATES.map((template) => (
                    <button
                      key={template.id}
                      type="button"
                      role="menuitem"
                      className="home-new-item"
                      onClick={() => { createFromTemplate(template); }}
                    >
                      {template.label}
                    </button>
                  ))}
                </div>
              </div>
            )}
          </div>
        </div>
      </header>

      {error !== undefined && (
        <p className="home-banner" role="alert">
          {error}
        </p>
      )}

      {toast !== undefined && (
        <p className="home-toast" role="status">
          {toast.message}
          {toast.undoId !== undefined && <button type="button" onClick={undoDelete} disabled={busy}>Undo</button>}
          <button type="button" className="home-toast-dismiss" aria-label="Dismiss" onClick={() => { setToast(undefined); }}>×</button>
        </p>
      )}

      {view === 'bin' && <BinView onRestored={() => { void reload(); }} onStatus={say} typeIcon={(type) => <TypeIcon type={type} />} />}

      {view === 'documents' && (state.kind === 'ok' || filtering) && !(state.kind === 'ok' && documents.length === 0 && !filtering) && (
        <div className="home-tools" role="search">
          <input
            ref={searchField}
            type="search"
            className="home-search"
            placeholder="Search titles (press /)"
            aria-label="Search documents by title"
            value={search}
            onChange={(e) => { setSearch(e.target.value); }}
            onKeyDown={(e) => {
              if (e.key === 'Escape') {
                setSearch('');
                setQuery('');
              }
            }}
          />
          <select className="home-type-filter" aria-label="Show only one type" value={typeFilter} onChange={(e) => { setTypeFilter(e.target.value as DocumentType | ''); }}>
            <option value="">All types</option>
            {DIAGRAM_TYPES.map((option) => <option key={option.type} value={option.type}>{option.label}</option>)}
          </select>
          {layout === 'grid' && (
            // The table sorts on its column headers; the grid has none, so it gets the same choice as a menu.
            <select
              className="home-type-filter"
              aria-label="Sort by"
              value={`${sort.field}:${sort.dir}`}
              onChange={(e) => {
                const [field, dir] = e.target.value.split(':') as [DocumentSort, 'asc' | 'desc'];
                setSort({ field, dir });
              }}
            >
              {SORT_CHOICES.map((choice) => <option key={choice.value} value={choice.value}>{choice.label}</option>)}
            </select>
          )}
          <div className="home-views home-layouts" role="group" aria-label="Layout">
            <button type="button" aria-pressed={layout === 'grid'} onClick={() => { chooseLayout('grid'); }}>Grid</button>
            <button type="button" aria-pressed={layout === 'list'} onClick={() => { chooseLayout('list'); }}>List</button>
          </div>
        </div>
      )}

      {view === 'documents' && state.kind === 'loading' && <p className="page-message">Loading documents…</p>}
      {view === 'documents' && state.kind === 'error' && <p className="page-message" role="alert">Cannot list documents: {state.message}</p>}
      {view === 'documents' && state.kind === 'ok' && documents.length === 0 && filtering && (
        <p className="home-no-match" role="status">
          No documents match{query === '' ? '' : ` “${query}”`}{typeFilter === '' ? '' : ` of that type`}.{' '}
          <button type="button" className="home-link-button" onClick={() => { setSearch(''); setQuery(''); setTypeFilter(''); }}>Clear search</button>
        </p>
      )}
      {view === 'documents' && state.kind === 'ok' && documents.length === 0 && !filtering && (
        <section className="home-empty" aria-labelledby="home-empty-title">
          <h2 id="home-empty-title" className="home-empty-title">No documents yet</h2>
          <p className="home-empty-line">Start with a blank flowchart or architecture diagram, or paste text and let Draughtsman draw it.</p>
          <div className="home-empty-cards">
            <button type="button" className="home-empty-card" onClick={() => { create('flowchart'); }} disabled={busy}>
              <span className="home-empty-card-title">Flowchart</span>
              <span className="home-empty-card-hint">Steps, decisions and outcomes</span>
            </button>
            <button type="button" className="home-empty-card" onClick={() => { create('architecture'); }} disabled={busy}>
              <span className="home-empty-card-title">Architecture</span>
              <span className="home-empty-card-hint">Services, data stores and how they connect</span>
            </button>
            {aiAvailable === true && (
              <button
                type="button"
                className="home-empty-card"
                disabled={busy}
                onClick={() => {
                  setBusy(true);
                  setError(undefined);
                  void createAndOpen('architecture', { openAi: true }).catch((cause: unknown) => {
                    setError(cause instanceof Error ? cause.message : 'Unknown error');
                    setBusy(false);
                  });
                }}
              >
                <span className="home-empty-card-title">From text</span>
                <span className="home-empty-card-hint">Describe it in words and let the AI draw it</span>
              </button>
            )}
          </div>
          <p className="home-empty-more" role="group" aria-label="More diagram types">
            <span>Or start from:</span>
            {DIAGRAM_TEMPLATES.map((template) => (
              <button key={template.id} type="button" className="home-link-button" title={template.hint} onClick={() => { createFromTemplate(template); }} disabled={busy}>{template.label}</button>
            ))}
          </p>
          <p className="home-empty-more">
            <button type="button" className="home-link-button" onClick={() => { setImportOpen(true); }} disabled={busy}>Import SQL</button>
            <button type="button" className="home-link-button" onClick={() => { setMermaidOpen(true); }} disabled={busy}>Import Mermaid</button>
            <button type="button" className="home-link-button" onClick={() => { setDrawioOpen(true); }} disabled={busy}>Import draw.io</button>
            <button type="button" className="home-link-button" onClick={() => { setVisioOpen(true); }} disabled={busy}>Import Visio</button>
            <button type="button" className="home-link-button" onClick={addSamples} disabled={busy}>Add four sample diagrams</button>
          </p>
          <p className="home-empty-note">The samples are a flowchart, an architecture, a sequence and an ER diagram. They are ordinary documents: edit or delete them freely.</p>
        </section>
      )}
      {view === 'documents' && state.kind === 'ok' && documents.length > 0 && layout === 'grid' && (
        <DocumentCards
          documents={documents}
          typeIcon={(type) => <TypeIcon type={type} />}
          title={titleControl}
          modified={(document) => {
            const time = relativeTime(document.updatedAt);
            return <span title={time.absolute}>{time.relative}</span>;
          }}
          actions={actionsMenu}
        />
      )}
      {view === 'documents' && state.kind === 'ok' && documents.length > 0 && layout === 'list' && (
        <table className="home-list">
          <thead>
            <tr>
              <th scope="col" aria-sort={ariaSort('type')}>
                <button type="button" className="home-sort" onClick={() => { sortBy('type'); }}>Type{sortMark('type')}</button>
              </th>
              <th scope="col" aria-sort={ariaSort('title')}>
                <button type="button" className="home-sort" onClick={() => { sortBy('title'); }}>Title{sortMark('title')}</button>
              </th>
              <th scope="col" aria-sort={ariaSort('modified')}>
                <button type="button" className="home-sort" onClick={() => { sortBy('modified'); }}>Last modified{sortMark('modified')}</button>
              </th>
              <th scope="col">
                <span className="visually-hidden">Actions</span>
              </th>
            </tr>
          </thead>
          <tbody>
            {documents.map((document) => {
              const time = relativeTime(document.updatedAt);
              return (
                <tr key={document.id}>
                  <td className="home-type">
                    <TypeIcon type={document.type} />
                  </td>
                  <td className="home-title-cell">{titleControl(document)}</td>
                  <td className="home-modified" title={time.absolute}>
                    {time.relative}
                  </td>
                  <td className="home-actions">{actionsMenu(document)}</td>
                </tr>
              );
            })}
          </tbody>
        </table>
      )}

      {importOpen && (
        <ImportSqlDialog
          onClose={() => { setImportOpen(false); }}
          onCreated={open}
        />
      )}

      {mermaidOpen && (
        <ImportMermaidDialog
          onClose={() => { setMermaidOpen(false); }}
          onCreated={open}
        />
      )}

      {drawioOpen && (
        <ImportDrawioDialog
          onClose={() => { setDrawioOpen(false); }}
          onCreated={open}
        />
      )}

      {visioOpen && (
        <ImportVisioDialog
          onClose={() => { setVisioOpen(false); }}
          onCreated={open}
        />
      )}

    </div>
  );
}