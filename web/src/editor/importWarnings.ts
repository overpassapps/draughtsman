/**
 * What an import's warnings add up to, in words that tell the kinds apart: a kind the importer chose from a shape's look
 * (`kind-guessed`) is a judgement to check, not something lost, so it is not reported as "skipped"; anything else (an unsupported
 * shape, a connector with a loose end, a colour with no equivalent) is. `page-selected` is neither: it is the importer saying which
 * page of a multi-page file it read, which the dialog's Page choice already shows, so it is listed but not counted.
 * ImportMermaidDialog has the same wording inline (QA-08); the draw.io dialog is the first to share it.
 */

const NOT_COUNTED = new Set(['page-selected']);

export function describeImportWarnings(warnings: readonly { code: string }[]): string {
  const guessed = warnings.filter((warning) => warning.code === 'kind-guessed').length;
  const other = warnings.filter((warning) => warning.code !== 'kind-guessed' && !NOT_COUNTED.has(warning.code)).length;
  const parts: string[] = [];
  if (guessed > 0) {
    parts.push(`${String(guessed)} ${guessed === 1 ? 'shape' : 'shapes'} could mean more than one thing, so ${guessed === 1 ? 'its kind was' : 'their kinds were'} chosen for you and listed below`);
  }
  if (other > 0) {
    parts.push(`${String(other)} ${other === 1 ? 'item was' : 'items were'} skipped or only partly understood`);
  }
  return parts.length === 0 ? '' : ` ${parts.join('; ')}:`;
}
