#!/usr/bin/env bash
# Tests for the customer documentation a release archive carries (scripts/lib/archive-docs.sh, called by publish.sh), run by
# scripts/verify-local.sh. Each rule is checked in both directions: the guides are there and no dead link is left, and the
# internal records are absent and a check that would catch a dead link really does.
#
#   scripts/archive-docs.test.sh
set -u

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# shellcheck source=lib/archive-docs.sh
source "$repo_root/scripts/lib/archive-docs.sh"
work="$(mktemp -d "${TMPDIR:-/tmp}/archive-docs-test.XXXXXX")"
trap 'rm -rf "${work:?}"' EXIT

failures=0
pass() { echo "PASS: $1"; }
fail() { echo "FAIL: $1"; failures=$((failures + 1)); }
present() { if [ -e "$1/$2" ]; then pass "$3"; else fail "$3 ($2 is missing)"; fi; }
absent() { if [ ! -e "$1/$2" ]; then pass "$3"; else fail "$3 ($2 is in the archive)"; fi; }

stage="$work/stage"
mkdir -p "$stage"
# A .md the publish step puts in the archive folder before the docs are added (Playwright's package carries some): its links must not be touched.
mkdir -p "$stage/.playwright/package"
printf 'See [the gone file](gone.md).\n' >"$stage/.playwright/package/README.md"
add_archive_docs "$repo_root" "$stage" >/dev/null 2>"$work/err" || { fail "add_archive_docs failed: $(cat "$work/err")"; }

# The guides a customer needs.
for guide in docs/index.md docs/upgrade-backup.md docs/troubleshooting.md docs/admin-guide.md docs/deployment.md docs/install-docker.md docs/install-linux.md docs/install-macos.md docs/install-windows.md docs/api.md docs/data-flow.md docs/pdf-export.md docs/air-gapped.md docs/draughtsman.example.yaml docs/spec/dsl.md docs/spec/dsl.grammar docs/examples/order-service.dsl INSTALL.md SECURITY.md SKILL.md CHANGELOG.md; do
  present "$stage" "$guide" "the archive carries $guide"
done

# What is internal.
absent "$stage" docs/spec/build-spec-v0.1.md "the build specification is not shipped"
absent "$stage" docs/adr "the ADRs are not shipped"
absent "$stage" docs/testing-installs.md "the install matrix notes (a maintainer page about scripts the archive does not carry) are not shipped"
absent "$stage" docs/examples/perf-2000n-2500e.json "the 2,000-node benchmark fixture is not shipped"
absent "$stage" README.md "the developer README is not shipped"

# No dead link in what was staged, and INSTALL.md reaches the guides from the archive root.
docs_only="$work/docs-only"
mkdir -p "$docs_only"
cp -R "$stage/docs" "$stage/INSTALL.md" "$stage/SECURITY.md" "$stage/SKILL.md" "$stage/CHANGELOG.md" "$docs_only/"
if out="$(node "$repo_root/scripts/lib/check-doc-links.mjs" "$docs_only")"; then pass "every relative link in the archive's guides resolves"; else fail "dead links in the archive: $out"; fi
if grep -q '](docs/install-docker.md)' "$stage/INSTALL.md" && ! grep -q '](install-docker.md)' "$stage/INSTALL.md"; then pass "INSTALL.md links the guides under docs/"; else fail "INSTALL.md still links siblings that are not next to it"; fi

# A link into a page the archive lacks is plain text now, its words kept.
if grep -q 'testing-installs.md' "$stage/docs/index.md" && ! grep -q '](testing-installs.md)' "$stage/docs/index.md"; then pass "a link to a page that is not shipped is plain text with its words kept"; else fail "docs/index.md still links testing-installs.md, or lost its words"; fi
if grep -q '(gone.md)' "$stage/.playwright/package/README.md"; then pass "a .md file outside the documentation is left untouched"; else fail "the rewrite touched a file that is not documentation"; fi

# The opposite direction: the same check fails when a guide is missing, so the rule above can fail.
broken="$work/broken"
cp -R "$docs_only" "$broken"
rm "$broken/docs/upgrade-backup.md"
if out="$(node "$repo_root/scripts/lib/check-doc-links.mjs" "$broken")"; then fail "the link check missed a removed guide"; else
  if grep -q 'upgrade-backup.md' <<<"$out"; then pass "the link check reports a removed guide"; else fail "the link check failed for the wrong reason: $out"; fi
fi

# And an unpruned copy of the repository's docs does have dead links, which is why the unlinking step exists.
raw="$work/raw"
mkdir -p "$raw"
stage_archive_docs "$repo_root" "$raw"
if node "$repo_root/scripts/lib/check-doc-links.mjs" "$raw" >/dev/null; then fail "the staged docs had no dead link before unlinking, so the unlinking step is untested"; else pass "the staged docs have dead links until they are unlinked"; fi

if [ "$failures" -gt 0 ]; then echo "$failures archive docs test(s) failed"; exit 1; fi
echo "all archive docs tests passed"
