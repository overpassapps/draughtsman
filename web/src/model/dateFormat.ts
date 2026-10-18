/**
 * The one date and time format the app shows (QA P14). `toLocaleString()` printed `01/10/2026, 02:35:07`, which a UK
 * and a US reader read as different days and which names no time zone. Everything now reads `1 Oct 2026, 02:35` in the
 * viewer's own time zone, with the month spelled out and no seconds; the zone abbreviation and the full date are on
 * hover (`dateTimeTitle`). Month names are fixed English strings rather than `Intl`'s, whose abbreviation of September
 * differs between engines, so the same instant reads the same in every browser and in jsdom.
 */

const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'] as const;

const two = (n: number): string => String(n).padStart(2, '0');

function parse(value: string | number | Date): Date | undefined {
  const date = value instanceof Date ? value : new Date(value);
  return Number.isNaN(date.getTime()) ? undefined : date;
}

/** `1 Oct 2026, 02:35` in the viewer's time zone; `fallback` (default: the input as given) when it is not a date. */
export function formatDateTime(value: string | number | Date, fallback?: string): string {
  const date = parse(value);
  if (date === undefined) {
    return fallback ?? String(value);
  }
  return `${String(date.getDate())} ${MONTHS[date.getMonth()] ?? ''} ${String(date.getFullYear())}, ${two(date.getHours())}:${two(date.getMinutes())}`;
}

/** `1 Oct 2026`, for places where a time of day adds nothing (a document older than a week). */
export function formatDate(value: string | number | Date, fallback?: string): string {
  const date = parse(value);
  if (date === undefined) {
    return fallback ?? String(value);
  }
  return `${String(date.getDate())} ${MONTHS[date.getMonth()] ?? ''} ${String(date.getFullYear())}`;
}

/** `02:35`, for a time that is plainly today. */
export function formatTime(value: string | number | Date, fallback?: string): string {
  const date = parse(value);
  return date === undefined ? (fallback ?? String(value)) : `${two(date.getHours())}:${two(date.getMinutes())}`;
}

/** The zone name the viewer's browser gives for that instant (`BST`, `GMT+1`, `UTC`), or the empty string. */
export function zoneAbbreviation(value: string | number | Date): string {
  const date = parse(value);
  if (date === undefined) {
    return '';
  }
  try {
    return new Intl.DateTimeFormat('en-GB', { timeZoneName: 'short' }).formatToParts(date).find((part) => part.type === 'timeZoneName')?.value ?? '';
  } catch {
    return '';
  }
}

/** The hover text: the exact time with seconds and the zone it is shown in, e.g. `1 Oct 2026, 02:35:07 BST`. */
export function dateTimeTitle(value: string | number | Date): string {
  const date = parse(value);
  if (date === undefined) {
    return '';
  }
  const zone = zoneAbbreviation(date);
  return `${formatDateTime(date)}:${two(date.getSeconds())}${zone === '' ? '' : ` ${zone}`}`;
}
