/** Ordering and wording for the Review panel (ReviewPanel.tsx), kept apart so the component file exports only a component. */

import type { CritiqueFinding } from '../model/historyClient.ts';

/** How long after the last edit a re-run waits, so typing a label does not ask the server on every keystroke. */
export const REVIEW_DEBOUNCE_MS = 800;

const SEVERITY_ORDER: Record<string, number> = { error: 0, warning: 1 };

/** Errors before warnings, then the analyser's own order (rule, then evidence), which is already stable. */
export function orderFindings(findings: readonly CritiqueFinding[]): CritiqueFinding[] {
  return findings
    .map((finding, index) => ({ finding, index }))
    .sort((a, b) => (SEVERITY_ORDER[a.finding.severity] ?? 2) - (SEVERITY_ORDER[b.finding.severity] ?? 2) || a.index - b.index)
    .map(({ finding }) => finding);
}

export function summaryLine(findings: readonly CritiqueFinding[]): string {
  if (findings.length === 0) {
    return 'No findings. The structure checks passed.';
  }
  const errors = findings.filter((finding) => finding.severity === 'error').length;
  const warnings = findings.length - errors;
  const plural = (n: number, word: string): string => `${String(n)} ${word}${n === 1 ? '' : 's'}`;
  const parts = [errors > 0 ? plural(errors, 'error') : undefined, warnings > 0 ? plural(warnings, 'warning') : undefined].filter((part): part is string => part !== undefined);
  return `${plural(findings.length, 'finding')}: ${parts.join(', ')}`;
}
