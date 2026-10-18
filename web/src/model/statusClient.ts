/**
 * The admin status page's data (docs/api.md "## About and status"): the layout service, PDF component, AI provider,
 * storage and data folder, which the product otherwise degrades around without saying so. Admin only (403 for anyone
 * else). The server omits null fields, so the optional keys here really are absent.
 */
export type SidecarStatusName = 'running' | 'starting' | 'unavailable' | 'stopped';

export interface AdminStatus {
  version: string;
  buildDate: string;
  dataFolder: string;
  host: 'console' | 'container' | 'systemd' | 'launchd' | 'windowsService';
  logHint: string;
  storage: { provider: string; file?: string };
  sidecar: { status: SidecarStatusName; version?: string; node?: string; restarts: number; error?: string; nodePath?: string; scriptPath?: string };
  pdf: { enabled: boolean; available: boolean; path?: string; hint?: string };
  ai: { enabled: boolean; provider?: string; model?: string };
  /** The connection check in a line: absent from a server older than the check. */
  connection?: ConnectionStatus;
}

export interface ConnectionStatus {
  peer: string;
  peerTrusted: boolean;
  forwardedHeadersSeen: boolean;
  scheme: string;
  throttleAddress: string;
  checks: { pass: number; warn: number; fail: number };
  problems: string[];
}

export class StatusError extends Error {
  readonly status: number;

  constructor(status: number, message: string) {
    super(message);
    this.name = 'StatusError';
    this.status = status;
  }
}

const SIDECAR_STATES: readonly string[] = ['running', 'starting', 'unavailable', 'stopped'];

export async function fetchAdminStatus(fetchImpl: typeof fetch = fetch): Promise<AdminStatus> {
  const response = await fetchImpl('/api/admin/status', { headers: { Accept: 'application/json' } });
  if (!response.ok) {
    throw new StatusError(response.status, response.status === 403 ? 'Only an administrator can see this.' : 'Could not read the system status.');
  }
  const body = (await response.json()) as Partial<AdminStatus> | null;
  if (body === null || typeof body.version !== 'string' || typeof body.dataFolder !== 'string' || body.sidecar === undefined || !SIDECAR_STATES.includes(body.sidecar.status) || body.pdf === undefined || body.ai === undefined || body.storage === undefined) {
    throw new StatusError(response.status, 'The system status was not in the expected shape.');
  }
  return body as AdminStatus;
}
