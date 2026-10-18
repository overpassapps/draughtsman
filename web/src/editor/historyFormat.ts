/** Small formatting helpers for the History panel (HistoryPanel.tsx), kept apart so the component file exports only a component. */
import { formatDate, formatDateTime } from '../model/dateFormat.ts';

/** "12.4 KB" from the snapshot's length in characters. */
export function formatSize(characters: number): string {
  if (characters < 1024) {
    return `${String(characters)} B`;
  }
  return `${(characters / 1024).toFixed(characters < 10 * 1024 ? 1 : 0)} KB`;
}

/** "user:Alice" reads "Alice"; "agent:claude-code/claude-fable-5-1" reads "claude-code (agent)". */
export function actorLabel(actor: string): string {
  if (actor.startsWith('agent:')) {
    const name = actor.slice('agent:'.length).split('/')[0] ?? actor;
    return `${name} (agent)`;
  }
  return actor.startsWith('user:') ? actor.slice('user:'.length) : actor;
}

/** A coarse relative time: "just now", "5 min ago", "3 h ago", "2 d ago", then the date. */
export function relativeTime(iso: string, now: number = Date.now()): string {
  const seconds = Math.max(0, Math.round((now - Date.parse(iso)) / 1000));
  if (seconds < 60) {
    return 'just now';
  }
  const minutes = Math.round(seconds / 60);
  if (minutes < 60) {
    return `${String(minutes)} min ago`;
  }
  const hours = Math.round(minutes / 60);
  if (hours < 24) {
    return `${String(hours)} h ago`;
  }
  const days = Math.round(hours / 24);
  return days < 14 ? `${String(days)} d ago` : formatDate(iso);
}

/**
 * A version's reason with any UTC stamp the server wrote into it ("Before restoring "v1" (2026-10-01 01:47:49Z)") shown as
 * the time it was where the reader is, in the app's one date format (model/dateFormat.ts): "1 Oct 2026, 02:47". The server keeps the UTC form for agents and the audit trail
 * (docs/api.md); only the panel translates it (QA P11).
 */
export function readableReason(reason: string): string {
  return reason.replace(/(\d{4}-\d{2}-\d{2}) (\d{2}:\d{2}:\d{2})Z/g, (stamp, date: string, time: string) => {
    const at = new Date(`${date}T${time}Z`);
    return Number.isNaN(at.getTime()) ? stamp : formatDateTime(at);
  });
}
