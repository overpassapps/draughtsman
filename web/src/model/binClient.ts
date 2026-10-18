/**
 * The bin (docs/api.md "Bin", design/atlas/document-list.md): what `DELETE /api/documents/{id}` moved aside, listed,
 * restored, or - as a second, separate step - removed for good. The server decides who sees which entry (an Editor
 * sees what they created or deleted, an administrator everything) and answers `canPurge` per entry; this only asks.
 */

import { csrfHeaders } from './csrf.ts';
import type { DocumentType } from './types.ts';

/** One bin entry, as `GET /api/bin` sends it. */
export interface BinEntry {
  id: string;
  title: string;
  type: DocumentType;
  createdAt: string;
  updatedAt: string;
  deletedAt: string;
  /** Display name of whoever deleted it, when they did. */
  deletedBy?: string;
  /** When the retention pass will remove it; absent when the instance keeps the bin for ever (`retention.binDays` of 0). */
  purgesAt?: string;
  /** Whether this caller may remove it permanently (an administrator, or its creator). */
  canPurge: boolean;
}

export interface BinListing {
  items: BinEntry[];
  total: number;
  offset: number;
  limit: number;
  /** `retention.binDays`; 0 or less means binned documents are kept until someone removes them. */
  retentionDays: number;
}

async function detail(response: Response, fallback: string): Promise<string> {
  try {
    const problem = (await response.json()) as { detail?: string; title?: string };
    return problem.detail ?? problem.title ?? fallback;
  } catch {
    return fallback;
  }
}

/** The bin this caller may see, newest deletion first. */
export async function listBin(fetchImpl: typeof fetch = fetch): Promise<BinListing> {
  const response = await fetchImpl('/api/bin?limit=200', { headers: { Accept: 'application/json' } });
  if (!response.ok) {
    throw new Error(`Listing the bin failed: the server returned ${String(response.status)}`);
  }
  return (await response.json()) as BinListing;
}

/** Takes a document out of the bin, whole. Resolves false when it is no longer there (restored or removed elsewhere). */
export async function restoreDocument(id: string, fetchImpl: typeof fetch = fetch): Promise<boolean> {
  const response = await fetchImpl(`/api/bin/${encodeURIComponent(id)}/restore`, {
    method: 'POST',
    headers: { Accept: 'application/json', ...csrfHeaders() },
  });
  if (response.status === 404) {
    return false;
  }
  if (!response.ok) {
    throw new Error(await detail(response, `Restoring the document failed: the server returned ${String(response.status)}`));
  }
  return true;
}

/** Removes a binned document for good: the explicit second step. Resolves false when it is no longer in the bin. */
export async function purgeDocument(id: string, fetchImpl: typeof fetch = fetch): Promise<boolean> {
  const response = await fetchImpl(`/api/bin/${encodeURIComponent(id)}`, {
    method: 'DELETE',
    headers: { Accept: 'application/json', ...csrfHeaders() },
  });
  if (response.status === 404) {
    return false;
  }
  if (!response.ok) {
    throw new Error(await detail(response, `Deleting the document permanently failed: the server returned ${String(response.status)}`));
  }
  return true;
}

/** Whole days left before the retention pass removes an entry, rounded up; undefined when nothing is scheduled. */
export function daysLeft(entry: BinEntry, now: number = Date.now()): number | undefined {
  if (entry.purgesAt === undefined) {
    return undefined;
  }
  return Math.max(0, Math.ceil((Date.parse(entry.purgesAt) - now) / 86_400_000));
}

/** The sentence under the bin's heading that says how long a deleted document stays. */
export function retentionLine(days: number): string {
  return days > 0
    ? `Deleted documents stay here for ${String(days)} day${days === 1 ? '' : 's'}, then are removed for good. Restore brings one back exactly as it was, with its history.`
    : 'Deleted documents stay here until someone deletes them permanently. Restore brings one back exactly as it was, with its history.';
}

