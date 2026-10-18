/**
 * The About screen's data (docs/api.md "## About and status"): the same body for every signed-in user, so an Editor can
 * read it. Nothing host-specific is in it; the admin status page has its own client (statusClient.ts). The server omits
 * null fields, so `docsUrl` is really absent when none is configured.
 */
export interface About {
  version: string;
  /** yyyy-MM-dd */
  buildDate: string;
  /** An email address (linked with mailto:) or any text. */
  supportContact: string;
  /** An absolute http(s) URL, `/docs/` while this server serves its own copy of the guides, or absent. */
  docsUrl?: string;
  /** Where this instance serves its third-party licences. */
  noticesUrl: string;
}

export class AboutError extends Error {
  readonly status: number;

  constructor(status: number, message: string) {
    super(message);
    this.name = 'AboutError';
    this.status = status;
  }
}

export async function fetchAbout(fetchImpl: typeof fetch = fetch): Promise<About> {
  const response = await fetchImpl('/api/about', { headers: { Accept: 'application/json' } });
  if (!response.ok) {
    throw new AboutError(response.status, 'Could not read the version details.');
  }
  const body = (await response.json()) as Partial<About> | null;
  if (body === null || typeof body.version !== 'string' || typeof body.buildDate !== 'string' || typeof body.supportContact !== 'string' || typeof body.noticesUrl !== 'string') {
    throw new AboutError(response.status, 'The version details were not in the expected shape.');
  }
  return body as About;
}

/** The link a support contact becomes: `mailto:` for something that looks like an address, otherwise none (shown as text). */
export function supportHref(contact: string): string | undefined {
  const trimmed = contact.trim();
  return /^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(trimmed) ? `mailto:${trimmed}` : undefined;
}
