/**
 * The home screen's grid view (design/atlas/document-list.md): one card per document with its thumbnail, type, title and
 * last-modified time, and the same actions as a table row. It owns layout only: what a title is (the open button, or the rename
 * field while renaming) and what the kebab holds stay in HomePage, which hands them in, so the grid and the table can never
 * disagree about what a document offers. Reading and focus order match the row: type, title, modified, actions, with the
 * preview image (named for the document) first. The whole card opens the document with the mouse through the title button's
 * stretched hit area, which is one focus stop, never two.
 */

import type { ReactNode } from 'react';
import type { DocumentSummary } from '../model/documentClient.ts';
import type { DocumentType } from '../model/types.ts';
import { DocumentThumbnail } from './DocumentThumbnail.tsx';

interface DocumentCardsProps {
  documents: DocumentSummary[];
  /** The type's glyph with its visually hidden name, as a row shows it. */
  typeIcon: (type: DocumentType) => ReactNode;
  /** The open button, or the rename field. */
  title: (document: DocumentSummary) => ReactNode;
  /** The relative last-modified time with the absolute one on hover. */
  modified: (document: DocumentSummary) => ReactNode;
  /** The kebab menu. */
  actions: (document: DocumentSummary) => ReactNode;
}

export function DocumentCards({ documents, typeIcon, title, modified, actions }: DocumentCardsProps) {
  return (
    <ul className="home-cards" aria-label="Documents">
      {documents.map((document) => (
        <li key={document.id} className="home-card">
          <DocumentThumbnail id={document.id} title={document.title} updatedAt={document.updatedAt} fallback={typeIcon(document.type)} />
          <div className="home-card-body">
            <div className="home-card-heading">
              <span className="home-card-type">{typeIcon(document.type)}</span>
              <span className="home-card-title">{title(document)}</span>
            </div>
            <div className="home-card-meta">{modified(document)}</div>
          </div>
          <div className="home-card-actions">{actions(document)}</div>
        </li>
      ))}
    </ul>
  );
}
