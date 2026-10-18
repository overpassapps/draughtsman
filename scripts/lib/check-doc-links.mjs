// Relative Markdown links in a folder: finds the ones that do not resolve to a file or folder inside it, and optionally
// turns them into plain text. A release archive carries the customer guides but not the specification, the ADRs or the
// source tree, so a guide's link to one of those would dangle; scripts/lib/archive-docs.sh unlinks them (the link text
// stays) and scripts/archive-docs.test.sh checks none is left.
//
//   node scripts/lib/check-doc-links.mjs <folder>            print one "file: target" line per broken link; exit 1 if any
//   node scripts/lib/check-doc-links.mjs <folder> --unlink   rewrite each broken [text](target) as text; exit 0
import { readdirSync, readFileSync, writeFileSync, existsSync, statSync } from 'node:fs';
import { join, dirname, resolve, relative } from 'node:path';

const root = resolve(process.argv[2] ?? '.');
const unlink = process.argv.includes('--unlink');
const broken = [];

function* markdownFiles(folder) {
  for (const entry of readdirSync(folder)) {
    const path = join(folder, entry);
    if (statSync(path).isDirectory()) yield* markdownFiles(path);
    else if (entry.endsWith('.md')) yield path;
  }
}

function isBroken(file, target) {
  if (/^([a-z][a-z0-9+.-]*:|#)/i.test(target)) return false;
  return !existsSync(resolve(dirname(file), decodeURIComponent(target.split('#')[0])));
}

// Fenced code is skipped: a link inside it is an example, not a link.
const fence = /^```[\s\S]*?^```/gm;
const link = /\[([^\]]*)\]\(([^)\s]+)\)/g;

for (const file of markdownFiles(root)) {
  const text = readFileSync(file, 'utf8');
  const fenced = [...text.matchAll(fence)].map((m) => [m.index, m.index + m[0].length]);
  const inFence = (index) => fenced.some(([from, to]) => index >= from && index < to);
  const rewritten = text.replace(link, (whole, label, target, index) => {
    if (inFence(index) || !isBroken(file, target)) return whole;
    broken.push(`${relative(root, file)}: ${target}`);
    return unlink ? label : whole;
  });
  if (unlink && rewritten !== text) writeFileSync(file, rewritten);
}

if (broken.length > 0 && !unlink) {
  console.log(broken.join('\n'));
  process.exit(1);
}
