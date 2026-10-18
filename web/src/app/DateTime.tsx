/** A date and time in the app's one format (model/dateFormat.ts): `1 Oct 2026, 02:35`, the zone and seconds on hover. */
import { dateTimeTitle, formatDateTime } from '../model/dateFormat.ts';

interface Props {
  /** An ISO 8601 instant; `undefined` shows `empty`. */
  iso: string | undefined;
  /** What to show for no value (`Never` for a last sign-in). */
  empty?: string;
}

export function DateTime({ iso, empty = 'Never' }: Props) {
  if (iso === undefined) {
    return <>{empty}</>;
  }
  return (
    <time dateTime={iso} title={dateTimeTitle(iso)}>
      {formatDateTime(iso)}
    </time>
  );
}
