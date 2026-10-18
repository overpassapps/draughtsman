/**
 * The paste-and-run prototype (design/atlas/try-page.md): DSL or Mermaid text in, a laid-out diagram on the canvas.
 * Flow is import -> layout -> save, each step optional-on-failure per docs/api.md so a down sidecar or a typo never
 * blanks what is on screen. Import leaves a node's geometry null unless the source itself gave a position, so when
 * layout cannot run this page places the rest on a plain fallback grid and says so, rather than leaving them to the
 * canvas's own provisional stack (`web/src/canvas/documentIndex.ts`), which reads as one unplaced column.
 * A run is saved unless it put a node somewhere the source did not ask for: placeholder grid positions are
 * scaffolding and must never reach a stored snapshot, where nothing would mark them as provisional. A document the
 * source positioned in full has nothing provisional about it, so a layout failure does not stop it being saved.
 * Saving is an upsert against the id this page last saved, so a second run of either source kind updates that
 * diagram rather than leaving another behind; an id this page has not created is never replaced, only sidestepped
 * for a server-assigned one. It moves the address bar to /d/{id} without a route change, so a reload lands on
 * DocumentPage and shows the same diagram; this page keeps rendering its own state so the failure banners stay
 * visible after a run.
 */

import { useCallback, useRef, useState } from 'react';
import { csrfHeaders } from '../model/csrf.ts';
import { readDocumentVersion } from '../model/documentVersion.ts';
import { parseDocument } from '../model/parse.ts';
import type { Document, Node as DiagramNode } from '../model/types.ts';
import { DocumentViewer } from './DocumentViewer.tsx';

type Source = 'dsl' | 'mermaid';

const DSL_SEED = `draughtsman 1
type architecture
title "Order flow"

node n_web service "Web app"
node n_api service "Orders API"
node n_db database "Postgres"

edge e_1 flow n_web -> n_api "HTTPS"
edge e_2 data n_api -> n_db "SQL"
`;

const MERMAID_SEED = `flowchart LR
  web[Web app] --> api[Orders API] --> db[(Postgres)]
`;

const FALLBACK_CELL_WIDTH = 160;
const FALLBACK_CELL_HEIGHT = 80;
const FALLBACK_GAP = 60;
const FALLBACK_COLUMNS = 4;
const PLACEHOLDER_POSITIONS =
  'the nodes the source left unplaced are on placeholder grid positions rather than a computed layout, their grouping is not shown, and the diagram was not saved.';
const AUTHORED_POSITIONS = 'the positions written in the source stand unchanged.';

/**
 * Why layout did not produce positions, in the server's own terms (LayoutEndpoints.cs): only 503 means the sidecar
 * is down. 504 is a sidecar that answered too slowly and 502 one that answered with a failure, and for both the
 * problem detail is the only account of what actually went wrong.
 */
function describeLayoutFailure(status: number, problem: ProblemBody): string {
  const because = problem.detail ? ` (${problem.detail})` : '';
  if (status === 503) {
    return 'Layout is unavailable (the sidecar is not running)';
  }
  if (status === 504) {
    return `Layout did not finish in time${because}`;
  }
  if (status === 502) {
    return `The layout engine failed${because}`;
  }
  return `Layout failed${because}`;
}

function needsPlacing(document: Document): boolean {
  return document.nodes.some((node) => node.geometry === undefined);
}

function withoutParent(node: DiagramNode): DiagramNode {
  const flat: DiagramNode = { ...node };
  delete flat.parent;
  return flat;
}

/**
 * The placement used when layout could not run, for the nodes the source itself left unplaced: a plain grid in
 * document order with fixed cells. It is not a layout and never claims to be one. A position the source wrote is
 * not a guess, so it is never touched, and neither is the containment around it; only where this function has had
 * to invent geometry is the parent link dropped, because a container box drawn around an invented cell (or an
 * invented box drawn around a real child) would clip that child out of the picture entirely.
 */
function withFallbackGrid(source: Document): Document {
  const authored = new Set(source.nodes.filter((node) => node.geometry !== undefined).map((node) => node.id));
  let placed = 0;
  const nodes = source.nodes.map((node) => {
    if (node.geometry !== undefined) {
      return node.parent !== undefined && !authored.has(node.parent) ? withoutParent(node) : node;
    }
    const column = placed % FALLBACK_COLUMNS;
    const row = Math.floor(placed / FALLBACK_COLUMNS);
    placed += 1;
    return {
      ...withoutParent(node),
      geometry: {
        x: column * (FALLBACK_CELL_WIDTH + FALLBACK_GAP),
        y: row * (FALLBACK_CELL_HEIGHT + FALLBACK_GAP),
        w: FALLBACK_CELL_WIDTH,
        h: FALLBACK_CELL_HEIGHT,
      },
    };
  });
  return { ...source, nodes };
}

interface ProblemBody {
  title?: string;
  detail?: string;
  line?: number;
  column?: number;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null;
}

function asProblem(value: unknown): ProblemBody {
  return isRecord(value) ? value : {};
}

/** Both import endpoints and /api/layout answer with { document, warnings }. */
function extractDocument(body: unknown): unknown {
  return isRecord(body) && 'document' in body ? body.document : undefined;
}

/**
 * The same envelope's warnings: what the importer or the layout engine approximated rather than carried faithfully.
 * The two endpoints spell them differently (docs/api.md): the importers send ImportWarning objects with a message
 * and an optional line, /api/layout sends plain strings. Both are read here so neither is silently dropped.
 */
function extractWarnings(body: unknown): string[] {
  const warnings: unknown = isRecord(body) ? body.warnings : undefined;
  if (!Array.isArray(warnings)) {
    return [];
  }
  return (warnings as unknown[]).flatMap((entry) => {
    if (typeof entry === 'string') {
      return entry.trim() === '' ? [] : [entry];
    }
    if (!isRecord(entry) || typeof entry.message !== 'string') {
      return [];
    }
    return [typeof entry.line === 'number' ? `line ${String(entry.line)}: ${entry.message}` : entry.message];
  });
}

async function readJson(response: Response): Promise<unknown> {
  try {
    return await response.json();
  } catch {
    return undefined;
  }
}

function describeUnreachable(error: unknown): string {
  const detail = error instanceof Error ? error.message : String(error);
  return `Cannot reach the Draughtsman API: ${detail}`;
}

function describeFailure(error: unknown): string {
  const detail = error instanceof Error ? error.message : String(error);
  return `The run could not be completed: ${detail}`;
}

type FetchOutcome =
  | { kind: 'ok'; body: unknown; version: string | undefined }
  | { kind: 'httpError'; status: number; problem: ProblemBody; body: unknown }
  | { kind: 'networkError'; error: unknown };

/** The base version a save is written from: the version (X-Document-Version, else ETag) of the write or read that produced the document being saved. */
async function send(
  method: 'POST' | 'PUT',
  url: string,
  body: BodyInit,
  contentType: string,
  baseVersion?: string,
): Promise<FetchOutcome> {
  const headers: Record<string, string> = { 'Content-Type': contentType, ...csrfHeaders() };
  if (baseVersion !== undefined) {
    headers['If-Match'] = baseVersion;
  }
  let response: Response;
  try {
    response = await fetch(url, { method, headers, body });
  } catch (error) {
    return { kind: 'networkError', error };
  }
  if (!response.ok) {
    const body = await readJson(response);
    return { kind: 'httpError', status: response.status, problem: asProblem(body), body };
  }
  return { kind: 'ok', body: await response.json(), version: readDocumentVersion(response) };
}

interface ParseFailure {
  message: string;
  where?: string | undefined;
}

export function TryPage() {
  const [source, setSource] = useState<Source>('dsl');
  const [dslText, setDslText] = useState(DSL_SEED);
  const [mermaidText, setMermaidText] = useState(MERMAID_SEED);
  const [running, setRunning] = useState(false);
  const [document, setDocument] = useState<Document | undefined>(undefined);
  const [parseFailure, setParseFailure] = useState<ParseFailure | undefined>(undefined);
  const [layoutNotice, setLayoutNotice] = useState<string | undefined>(undefined);
  const [runError, setRunError] = useState<string | undefined>(undefined);
  const [warnings, setWarnings] = useState<readonly string[]>([]);
  const [savedId, setSavedId] = useState<string | undefined>(undefined);
  const [idTaken, setIdTaken] = useState<string | undefined>(undefined);
  // Survives the per-run reset of savedId: it is the upsert target, not the readout.
  const lastSavedId = useRef<string | undefined>(undefined);
  // The version this page last wrote, sent as the base version of the next save. Without it the server refuses the
  // save rather than letting it overwrite whatever landed in between; a save this page cannot place is reported.
  const lastSavedVersion = useRef<string | undefined>(undefined);

  const text = source === 'dsl' ? dslText : mermaidText;
  const setText = source === 'dsl' ? setDslText : setMermaidText;

  const run = useCallback(async () => {
    setRunning(true);
    setParseFailure(undefined);
    setLayoutNotice(undefined);
    setRunError(undefined);
    setWarnings([]);
    setSavedId(undefined);
    setIdTaken(undefined);

    try {
      const importEndpoint = source === 'dsl' ? '/api/import/dsl' : '/api/import/mermaid';
      const importOutcome = await send('POST', importEndpoint, text, 'text/plain');
      if (importOutcome.kind === 'networkError') {
        setRunError(describeUnreachable(importOutcome.error));
        return;
      }
      if (importOutcome.kind === 'httpError') {
        const { problem } = importOutcome;
        const reported = extractWarnings(importOutcome.body);
        if (reported.length > 0) {
          setParseFailure({ message: reported.join('; ') });
          return;
        }
        const where = problem.line !== undefined ? `line ${String(problem.line)}${problem.column !== undefined ? `, column ${String(problem.column)}` : ''}` : undefined;
        setParseFailure({ message: problem.detail ?? problem.title ?? `The ${source === 'dsl' ? 'DSL' : 'Mermaid'} could not be read.`, where });
        return;
      }
      const imported = parseDocument(extractDocument(importOutcome.body));
      const collected = extractWarnings(importOutcome.body);
      setWarnings(collected);

      const layoutOutcome = await send('POST', '/api/layout', JSON.stringify(imported), 'application/json');
      // Layout is what places a node the source left unplaced. With none of those, a layout failure costs nothing:
      // the picture is exactly what was written, so there is no guess to label and nothing provisional to withhold.
      const guessing = needsPlacing(imported);
      if (layoutOutcome.kind === 'networkError') {
        setDocument(guessing ? withFallbackGrid(imported) : imported);
        setLayoutNotice(`Layout did not run, so ${guessing ? PLACEHOLDER_POSITIONS : AUTHORED_POSITIONS}`);
        setRunError(describeUnreachable(layoutOutcome.error));
        return;
      }

      let laidOut: Document;
      if (layoutOutcome.kind === 'httpError') {
        setLayoutNotice(`${describeLayoutFailure(layoutOutcome.status, layoutOutcome.problem)}, so ${guessing ? PLACEHOLDER_POSITIONS : AUTHORED_POSITIONS}`);
        if (guessing) {
          setDocument(withFallbackGrid(imported));
          return;
        }
        laidOut = imported;
      } else {
        laidOut = parseDocument(extractDocument(layoutOutcome.body));
        setWarnings([...collected, ...extractWarnings(layoutOutcome.body)]);
      }

      // Every run after the first updates the document this page already saved, whichever source kind it came from.
      // Replacing is only ever aimed at an id this page created: a DSL id is derived from the title alone, so the
      // id a source asks for may well belong to someone else's document, and this page must not write over it.
      const target = lastSavedId.current;
      const toSave = target === undefined ? laidOut : { ...laidOut, id: target };
      setDocument(toSave);

      const create = () => send('POST', '/api/documents', JSON.stringify(laidOut), 'application/json');
      const createUnderANewId = () => {
        const withoutId: Partial<Document> = { ...laidOut };
        delete withoutId.id;
        return send('POST', '/api/documents', JSON.stringify(withoutId), 'application/json');
      };
      // createdAt and createdBy belong to the stored document; the server keeps its own when they are left out.
      const replace = (id: string) => {
        const meta: Partial<Document['meta']> = { ...laidOut.meta };
        delete meta.createdAt;
        delete meta.createdBy;
        return send('PUT', `/api/documents/${encodeURIComponent(id)}`, JSON.stringify({ ...laidOut, id, meta }), 'application/json', lastSavedVersion.current);
      };

      let saveOutcome = target === undefined ? await create() : await replace(target);
      if (saveOutcome.kind === 'httpError' && saveOutcome.status === 409) {
        setIdTaken(laidOut.id);
        saveOutcome = await createUnderANewId();
      } else if (saveOutcome.kind === 'httpError' && saveOutcome.status === 404) {
        // The document this page saved earlier is gone (deleted, or a fresh data folder): stop aiming at it.
        lastSavedId.current = undefined;
        lastSavedVersion.current = undefined;
        saveOutcome = await create();
      }
      if (saveOutcome.kind === 'networkError') {
        setRunError(describeUnreachable(saveOutcome.error));
        return;
      }
      if (saveOutcome.kind === 'httpError') {
        setRunError(saveOutcome.problem.detail ? `Could not save the document: ${saveOutcome.problem.detail}` : 'Could not save the document.');
        return;
      }
      const saved = parseDocument(saveOutcome.body);
      lastSavedId.current = saved.id;
      lastSavedVersion.current = saveOutcome.version;
      setSavedId(saved.id);
      // Running again is this page's core interaction, and every run resolves to the same id: pushing each time
      // would stack entries that Back has to walk through one by one.
      const path = `/d/${encodeURIComponent(saved.id)}`;
      if (window.location.pathname === path) {
        window.history.replaceState(null, '', path);
      } else {
        window.history.pushState(null, '', path);
      }
    } catch (error) {
      // A body the model cannot express, or a response that is not JSON at all: say so rather than resetting silently.
      setRunError(describeFailure(error));
    } finally {
      setRunning(false);
    }
  }, [source, text]);

  return (
    <div className="try-page">
      <div className="try-controls">
        <h1 className="try-title">Paste a diagram</h1>
        <div className="try-source-toggle" role="radiogroup" aria-label="Source format">
          <label>
            <input type="radio" name="source" value="dsl" checked={source === 'dsl'} onChange={() => { setSource('dsl'); }} />
            DSL
          </label>
          <label>
            <input type="radio" name="source" value="mermaid" checked={source === 'mermaid'} onChange={() => { setSource('mermaid'); }} />
            Mermaid
          </label>
        </div>
        <textarea
          className="try-textarea"
          value={text}
          onChange={(e) => { setText(e.target.value); }}
          spellCheck={false}
          aria-label={`${source === 'dsl' ? 'DSL' : 'Mermaid'} source`}
        />
        <div className="try-actions">
          <button type="button" onClick={() => { void run(); }} disabled={running || text.trim() === ''}>
            {running ? 'Running…' : 'Run'}
          </button>
          {savedId !== undefined && <span className="try-saved">Saved as {savedId}</span>}
        </div>
        {idTaken !== undefined && (
          <p className="try-warnings try-id-taken" role="status">
            The id {idTaken} already belongs to another document, so this was saved under a new id instead of replacing it.
          </p>
        )}
        {parseFailure && (
          <p className="try-banner try-banner-error" role="alert">
            {parseFailure.where ? `${parseFailure.where}: ` : ''}
            {parseFailure.message}
          </p>
        )}
        {layoutNotice && (
          <p className="try-banner try-banner-warning" role="status">
            {layoutNotice}
          </p>
        )}
        {runError && (
          <p className="try-banner try-banner-error" role="alert">
            {runError}
          </p>
        )}
        {warnings.length > 0 && (
          <p className="try-warnings" role="status">
            {warnings.length === 1 ? '1 warning: ' : `${String(warnings.length)} warnings: `}
            {warnings.join('; ')}
          </p>
        )}
      </div>
      <div className="try-canvas">{document && <DocumentViewer document={document} />}</div>
    </div>
  );
}
