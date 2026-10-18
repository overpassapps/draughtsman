#!/usr/bin/env bash
# Checks a release must pass before anything is built (PK-07). Sourced by scripts/release.sh; each function prints why it
# refused on stderr and returns non-zero, so scripts/release.test.sh can drive them one at a time.

# guard_build_date DATE: a real calendar date, written YYYY-MM-DD, not in the future. The date is stamped into the build and shown on the
# About page, so it is pinned on purpose: a release is reproducible and never defaults to "whenever the script ran".
guard_build_date() {
  local date_value="${1:-}"
  if [ -z "$date_value" ]; then
    echo "release: --build-date YYYY-MM-DD is required: a release pins the build date, it never defaults to today" >&2
    return 1
  fi
  if ! node -e '
    const value = process.argv[1];
    if (!/^\d{4}-\d{2}-\d{2}$/.test(value)) process.exit(1);
    const parsed = new Date(`${value}T00:00:00Z`);
    if (Number.isNaN(parsed.getTime()) || parsed.toISOString().slice(0, 10) !== value) process.exit(1);
    // A day of slack for a release cut just after midnight UTC somewhere else; beyond that the date is wrong.
    process.exit(parsed.getTime() > Date.now() + 36 * 3600 * 1000 ? 2 : 0);
  ' "$date_value"; then
    echo "release: --build-date '$date_value' must be a real date, YYYY-MM-DD, and not in the future" >&2
    return 1
  fi
}

# guard_upgrade_fixture VERSION FIXTURES_DIR MIGRATIONS_DIR: a release is cut only when its own version has an upgrade fixture
# (docs/upgrade-backup.md, "The release rule"): tests/upgrade-fixtures/v<VERSION>/, made by scripts/make-upgrade-fixture.sh from a build of
# this release, so the NEXT release is proven to open the data this one writes. The fixture must name this version, hold both providers'
# data and expectations, and end at the newest migration in the tree: a fixture taken before a later migration was added does not
# describe this release's data folder and is refused as stale.
guard_upgrade_fixture() {
  local version="${1:-}" fixtures="${2:-}" migrations="${3:-}"
  if [ -z "$version" ]; then
    echo "release: the version could not be read from Directory.Build.props (or --version); cannot look for its upgrade fixture" >&2
    return 1
  fi
  local fixture="$fixtures/v$version"
  if [ ! -d "$fixture" ] || [ ! -f "$fixture/manifest.json" ]; then
    echo "release: version $version has no upgrade fixture ($fixture/manifest.json is missing). A release is cut only after its data folder is snapshotted: from this release's checkout run  scripts/make-upgrade-fixture.sh v$version  then commit tests/upgrade-fixtures/v$version/ (docs/upgrade-backup.md, 'The release rule')." >&2
    return 1
  fi
  local newest
  newest="$(ls "$migrations" 2>/dev/null | grep -E '^[0-9]{14}_.+\.cs$' | grep -v -E '\.Designer\.cs$|Snapshot' | sed 's/\.cs$//' | sort | tail -1 || true)"
  local verdict
  verdict="$(node -e '
    const fs = require("fs");
    const [dir, version, newest] = process.argv.slice(1);
    let m;
    try { m = JSON.parse(fs.readFileSync(`${dir}/manifest.json`, "utf8")); } catch { console.log("manifest.json is not valid JSON"); process.exit(0); }
    const problems = [];
    if (m.label !== `v${version}`) problems.push(`its label is ${JSON.stringify(m.label)}, not "v${version}"`);
    if (m.build?.version !== version) problems.push(`it was made by build version ${JSON.stringify(m.build?.version)}, not ${version}`);
    const migrations = m.build?.migrations ?? [];
    if (!newest) problems.push("no migrations were found in the tree to compare with");
    else if (migrations[migrations.length - 1] !== newest) problems.push(`it ends at migration ${migrations[migrations.length - 1] ?? "(none)"} but the tree ends at ${newest}: it is stale, make it again from this release`);
    for (const p of ["sqlite", "postgres"]) {
      for (const f of ["data-folder.tar.gz", "expected.json"]) if (!fs.existsSync(`${dir}/${p}/${f}`)) problems.push(`${p}/${f} is missing`);
    }
    if (!fs.existsSync(`${dir}/postgres/dump.sql.gz`)) problems.push("postgres/dump.sql.gz is missing");
    console.log(problems.join("; "));
  ' "$fixture" "$version" "$newest")"
  if [ -n "$verdict" ]; then
    echo "release: the upgrade fixture $fixture is not usable: $verdict" >&2
    return 1
  fi
}

# release_version FILE: the <Version> of Directory.Build.props, the version a release is cut as.
release_version() {
  sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$1" | head -1
}
