#!/usr/bin/env bash
# The customer documentation a release archive carries (sourced by scripts/publish.sh; driven by scripts/archive-docs.test.sh).
#
# The archive gets the guides a customer or operator reads: docs/*.md, the DSL page and its grammar, the example documents
# those pages point at, the annotated example config, and the root notes (SKILL, CHANGELOG, SECURITY) that docs/index.md
# links to as ../X. It does not get the internal records: the build specification and its amendments, the ADRs, the developer README or the large benchmark
# fixtures. A guide's link to anything not shipped is turned into plain text (check-doc-links.mjs --unlink), so the
# archive has no dead link. docs/install.md is also copied to the archive root as INSTALL.md by publish.sh, whose
# relative links are rewritten (archive_install_page) to work from the root as well as from docs/.

# Files under docs/ that are internal design or maintainer notes, never shipped.
ARCHIVE_DOCS_EXCLUDED=(testing-installs.md)

# stage_archive_docs REPO_ROOT STAGE: copies the customer documentation into STAGE/docs and the root notes into STAGE.
stage_archive_docs() {
  local repo="$1" stage="$2" f base skip
  mkdir -p "$stage/docs/spec"
  for f in "$repo"/docs/*.md; do
    base="$(basename "$f")"
    skip=0
    for excluded in "${ARCHIVE_DOCS_EXCLUDED[@]}"; do [ "$base" = "$excluded" ] && skip=1; done
    [ "$skip" = 1 ] || cp "$f" "$stage/docs/$base"
  done
  cp "$repo/docs/draughtsman.example.yaml" "$stage/docs/draughtsman.example.yaml"
  cp "$repo/docs/spec/dsl.md" "$repo/docs/spec/dsl.grammar" "$stage/docs/spec/"
  # Small example documents only: not the 2,000-node benchmark nor the editor-session fixture.
  mkdir -p "$stage/docs/examples"
  for f in "$repo"/docs/examples/*; do
    case "$(basename "$f")" in perf-2000n-2500e.json|editor-session.json) continue ;; esac
    cp "$f" "$stage/docs/examples/"
  done
  for f in SKILL.md CHANGELOG.md SECURITY.md; do
    [ -f "$repo/$f" ] && cp "$repo/$f" "$stage/$f"
  done
  return 0
}

# archive_install_page SOURCE DEST: docs/install.md as the archive's root INSTALL.md. Its links name sibling pages
# (install-docker.md) because it lives in docs/ in the repository; from the archive root they are under docs/.
archive_install_page() {
  sed -E 's#\]\(((install-[a-z]+|deployment|upgrade-backup|index|admin-guide|troubleshooting)\.md)#](docs/\1#g' "$1" >"$2"
}

# add_archive_docs REPO_ROOT STAGE: everything above, in the order it must happen, into the archive folder STAGE. The
# documentation is assembled in a scratch folder first so the link rewrite touches only these pages, never the .md files a
# dotnet publish puts in STAGE (the Playwright package carries some). Dangling links become plain text.
add_archive_docs() {
  local repo="$1" stage="$2" scratch
  scratch="$(mktemp -d "${TMPDIR:-/tmp}/archive-docs.XXXXXX")"
  # Any step failing stops the rest (publish.sh runs under set -e, but a function in an || chain would not inherit it).
  stage_archive_docs "$repo" "$scratch" \
    && archive_install_page "$repo/docs/install.md" "$scratch/INSTALL.md" \
    && node "$(dirname "${BASH_SOURCE[0]}")/check-doc-links.mjs" "$scratch" --unlink \
    && cp -R "$scratch"/. "$stage"/
  local status=$?
  rm -rf "${scratch:?}"
  return $status
}
