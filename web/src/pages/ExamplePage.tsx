import { useMemo } from 'react';
import orderService from '../../../docs/examples/order-service.json';
import { parseDocument } from '../model/parse.ts';
import { DocumentViewer } from './DocumentViewer.tsx';

/** Renders the shared example bundled at build time, so the renderer can be exercised before the documents API exists. */
export function ExamplePage() {
  const document = useMemo(() => parseDocument(orderService), []);
  return <DocumentViewer document={document} />;
}
