#!/usr/bin/env bash
# Snapshots a build's own data folder into tests/upgrade-fixtures/<label>/ (docs/upgrade-backup.md "The release rule";
# tests/upgrade-fixtures/README.md). This IS the release process: a release is cut only after its version has a fixture
# (scripts/release.sh refuses otherwise), so every later build is proven to open the data this one wrote.
#
#   scripts/make-upgrade-fixture.sh v0.2.0                          build THIS checkout, fill an instance, snapshot it
#   scripts/make-upgrade-fixture.sh m3-bin --source /scratch/clone  an older commit checked out elsewhere
#   scripts/make-upgrade-fixture.sh v0.2.0 --exe ./draughtsman      an unpacked release archive instead of a build
#   options: --provider sqlite|postgres|both (default both; postgres needs docker)   --key-ring none|key (default key)
#            --out <dir> (default tests/upgrade-fixtures)   --scratch <dir>   --keep (leave the scratch folder)   --force
#
# What it does, per provider: starts the build on a scratch data folder and a free random port (never a fixed port, never
# your own data), fills it through the real HTTP API with throwaway data (scripts/upgrade-fixture/populate.mjs: users with known
# passwords, tokens, documents of every type including a large one and a fly-through, versions, a bin, branding, themes, AI
# settings with a stored key, security events), checks the populated instance with verify.mjs, stops it by its own
# pid, and writes the data folder (and, for Postgres, a plain-SQL dump) plus expected.json and manifest.json.
# Everything in a fixture is throwaway and says so; the key-encryption key it records signs nothing real.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
here="$repo_root/scripts/upgrade-fixture"

label="${1:-}"
case "$label" in
  ""|-h|--help) sed -n '2,/^set -euo/p' "$0" | sed '$d' | sed 's/^# \{0,1\}//'; exit 0 ;;
esac
shift
if ! [[ "$label" =~ ^[a-z0-9][a-z0-9._-]*$ ]]; then
  echo "make-upgrade-fixture: the label '$label' must be lower-case letters, digits, '.', '_' or '-' (for example v0.2.0)" >&2
  exit 2
fi

source_dir="$repo_root"
exe=""
providers="both"
key_ring="key"
out_root="$repo_root/tests/upgrade-fixtures"
scratch=""
keep=0
force=0
postgres_image="${DRAUGHTSMAN_FIXTURE_POSTGRES_IMAGE:-postgres:17-alpine}"
while [ $# -gt 0 ]; do
  case "$1" in
    --source) source_dir="$(cd "${2:?--source needs a folder}" && pwd)"; shift 2 ;;
    --exe) exe="$(cd "$(dirname "${2:?--exe needs a path}")" && pwd)/$(basename "$2")"; shift 2 ;;
    --provider) providers="${2:?}"; shift 2 ;;
    --key-ring) key_ring="${2:?}"; shift 2 ;;
    --out) out_root="${2:?}"; shift 2 ;;
    --scratch) scratch="${2:?}"; shift 2 ;;
    --keep) keep=1; shift ;;
    --force) force=1; shift ;;
    *) echo "make-upgrade-fixture: unknown option $1" >&2; exit 2 ;;
  esac
done
case "$providers" in sqlite|postgres|both) ;; *) echo "make-upgrade-fixture: --provider is sqlite, postgres or both" >&2; exit 2 ;; esac
case "$key_ring" in none|key) ;; *) echo "make-upgrade-fixture: --key-ring is none or key" >&2; exit 2 ;; esac

fixture_dir="$out_root/$label"
if [ -e "$fixture_dir" ] && [ "$force" != "1" ]; then
  echo "make-upgrade-fixture: $fixture_dir already exists; a released version's fixture is never silently replaced (use --force on purpose)" >&2
  exit 1
fi
for tool in node tar gzip sqlite3 curl; do
  command -v "$tool" >/dev/null || { echo "make-upgrade-fixture: $tool is required on PATH" >&2; exit 1; }
done
if [ "$providers" != "sqlite" ]; then
  command -v docker >/dev/null || { echo "make-upgrade-fixture: docker is required for the Postgres fixture (or pass --provider sqlite)" >&2; exit 1; }
fi

# The scratch folder must be outside $HOME: a stray node_modules or node shim up the tree hijacks npm run and the sidecar build.
if [ -z "$scratch" ]; then scratch="$(mktemp -d "${TMPDIR:-/tmp}/make-upgrade-fixture.XXXXXX")"; else mkdir -p "$scratch"; fi
scratch="$(cd "$scratch" && pwd -P)"
case "$scratch" in "$HOME"/*) echo "make-upgrade-fixture: scratch folder $scratch is under \$HOME; use --scratch outside it" >&2; exit 1 ;; esac

server_pid=""
pg_container=""
cleanup() {
  if [ -n "$server_pid" ] && kill -0 "$server_pid" 2>/dev/null; then kill "$server_pid" 2>/dev/null || true; wait "$server_pid" 2>/dev/null || true; fi
  if [ -n "$pg_container" ]; then docker rm -f "$pg_container" >/dev/null 2>&1 || true; fi
  if [ "$keep" != "1" ]; then rm -rf "${scratch:?}"; else echo "make-upgrade-fixture: scratch kept at $scratch"; fi
}
trap cleanup EXIT

free_port() { node -e 'const s=require("net").createServer();s.listen(0,"127.0.0.1",()=>{console.log(s.address().port);s.close()})'; }

# ------------------------------------------------------------------------------------------------ the build
build_dir=""
examples_dir="$source_dir/docs/examples"
commit="unknown"
if [ -n "$exe" ]; then
  build_dir="$(dirname "$exe")"
  commit="$(git -C "$source_dir" rev-parse HEAD 2>/dev/null || echo unknown)"
else
  [ -d "$source_dir/src/Draughtsman.Server" ] || { echo "make-upgrade-fixture: $source_dir is not a Draughtsman checkout" >&2; exit 1; }
  commit="$(git -C "$source_dir" rev-parse HEAD 2>/dev/null || echo unknown)"
  echo "make-upgrade-fixture: building $source_dir ($commit)"
  # Node 22 must be first on PATH: the Sidecar project runs npm ci and bundles the sidecar as part of the build.
  (cd "$source_dir" && dotnet build src/Draughtsman.Server -v:minimal >"$scratch/build.log" 2>&1) || { tail -30 "$scratch/build.log" >&2; echo "make-upgrade-fixture: the build failed" >&2; exit 1; }
  build_dir="$source_dir/src/Draughtsman.Server/bin/Debug/net10.0"
  exe="$build_dir/draughtsman"
fi
[ -x "$exe" ] || { echo "make-upgrade-fixture: no executable at $exe" >&2; exit 1; }

# The Data Protection payloads (the stored AI key) are bound to the content root: the cwd of a build tree, the folder of a
# packaged install. The manifest records it so a test can open the ring the way the build that wrote it did.
content_root="$build_dir/"

kek="$(node -e 'console.log(require("crypto").randomBytes(32).toString("base64"))')"

# ------------------------------------------------------------------------------------------------ one provider
make_one() {
  local provider="$1"
  local data="$scratch/data-$provider"
  local port log
  rm -rf "$data"; mkdir -p "$data"
  port="$(free_port)"
  log="$scratch/server-$provider.log"
  local conn=""
  if [ "$provider" = "postgres" ]; then
    local pgport
    pgport="$(free_port)"
    pg_container="ds-fixture-pg-$$"
    docker run -d --rm --name "$pg_container" -e POSTGRES_PASSWORD=fixture-THROWAWAY-pg -e POSTGRES_DB=draughtsman -p "127.0.0.1:$pgport:5432" "$postgres_image" >/dev/null
    local i
    for i in $(seq 1 150); do docker exec "$pg_container" pg_isready -U postgres -d draughtsman >/dev/null 2>&1 && break; sleep 1; done
    docker exec "$pg_container" pg_isready -U postgres -d draughtsman >/dev/null || { echo "make-upgrade-fixture: the Postgres container did not come up" >&2; exit 1; }
    sleep 2
    conn="Host=127.0.0.1;Port=$pgport;Database=draughtsman;Username=postgres;Password=fixture-THROWAWAY-pg"
  fi

  {
    echo "# Written by scripts/make-upgrade-fixture.sh for the throwaway fixture instance '$label'."
    echo "storage:"
    echo "  provider: $provider"
    [ -n "$conn" ] && echo "  connectionString: \"$conn\""
    echo "retention:"
    echo "  preMigrationCopies: 3"
    echo "about:"
    echo "  supportContact: fixture-support@example.invalid"
  } >"$data/draughtsman.yaml"

  local env_args=(DRAUGHTSMAN_DATA="$data" ASPNETCORE_URLS="http://127.0.0.1:$port" "PATH=$PATH" "HOME=$scratch" "TMPDIR=$scratch")
  if [ "$key_ring" = "key" ]; then
    env_args+=(DRAUGHTSMAN_KEY_RING_PROTECTION=key DRAUGHTSMAN_KEY_ENCRYPTION_KEY="$kek")
  else
    env_args+=(DRAUGHTSMAN_KEY_RING_PROTECTION=none)
  fi
  echo "make-upgrade-fixture: starting $label ($provider) on 127.0.0.1:$port"
  # exec, so the pid kept is the server's own (a plain subshell would be killed while the server lived on, orphaned).
  (cd "$build_dir" && exec env -i "${env_args[@]}" "$exe" >"$log" 2>&1) &
  server_pid=$!
  local up=0 i
  for i in $(seq 1 240); do
    curl -s "http://127.0.0.1:$port/api/health" >/dev/null 2>&1 && { up=1; break; }
    kill -0 "$server_pid" 2>/dev/null || break
    sleep 0.5
  done
  if [ "$up" != "1" ]; then tail -40 "$log" >&2; echo "make-upgrade-fixture: the server did not start" >&2; exit 1; fi
  local code
  code="$(grep -o 'setup code: [A-Z0-9]*' "$log" | head -1 | awk '{print $3}' || true)"

  local expected="$scratch/expected-$provider.json"
  node "$here/populate.mjs" --base "http://127.0.0.1:$port" ${code:+--setup-code "$code"} --examples "$examples_dir" --out "$expected" --label "$label"
  node "$here/verify.mjs" --base "http://127.0.0.1:$port" --expected "$expected" --self-check
  # Anything the populated instance logged at warning or worse is worth a look before it becomes a fixture.
  grep -E '^(warn|fail|crit)' "$log" | sort | uniq -c | sort -rn | head -10 || true

  kill "$server_pid"
  # A graceful stop checkpoints the write-ahead log; wait for the process to be gone, never assume.
  local waited=0
  while kill -0 "$server_pid" 2>/dev/null && [ "$waited" -lt 60 ]; do sleep 0.5; waited=$((waited + 1)); done
  if kill -0 "$server_pid" 2>/dev/null; then echo "make-upgrade-fixture: the server (pid $server_pid) did not stop" >&2; exit 1; fi
  wait "$server_pid" 2>/dev/null || true
  server_pid=""

  local pkg="$fixture_dir/$provider"
  mkdir -p "$pkg"
  if [ "$provider" = "sqlite" ]; then
    # A graceful stop folds the write-ahead log in; make sure, so the snapshot is one self-contained file.
    sqlite3 "$data/data.db" 'PRAGMA wal_checkpoint(TRUNCATE);' >/dev/null
    rm -f "$data/data.db-wal" "$data/data.db-shm"
  else
    # Plain SQL with one INSERT per row, so a test can run it through a driver. The \restrict lines newer pg_dump versions add are psql
    # meta-commands carrying a random token: no driver can run them, and they would make every dump differ, so they are dropped.
    docker exec "$pg_container" pg_dump -U postgres --inserts --no-owner --no-acl draughtsman | grep -v -E '^\\(un)?restrict ' | gzip -9 >"$pkg/dump.sql.gz"
    docker rm -f "$pg_container" >/dev/null 2>&1 || true
    pg_container=""
    # The connection string is a placeholder in the fixture; the test substitutes its own database.
    sed -i.bak 's|connectionString: ".*"|connectionString: "__TEST_POSTGRES__"|' "$data/draughtsman.yaml" && rm -f "$data/draughtsman.yaml.bak"
  fi
  rm -rf "$data/logs" "$data/setup-code.txt"
  COPYFILE_DISABLE=1 tar -C "$data" -czf "$pkg/data-folder.tar.gz" .
  cp "$expected" "$pkg/expected.json"
  node "$here/manifest.mjs" provider --data "$data" --pkg "$pkg" --provider "$provider"
  echo "make-upgrade-fixture: $provider fixture written to $pkg"
}

mkdir -p "$fixture_dir"
if [ "$providers" = "sqlite" ] || [ "$providers" = "both" ]; then make_one sqlite; fi
if [ "$providers" = "postgres" ] || [ "$providers" = "both" ]; then make_one postgres; fi

node "$here/manifest.mjs" fixture --dir "$fixture_dir" --label "$label" --commit "$commit" --source "$source_dir" \
  --key-ring "$key_ring" --kek "$kek" --content-root "$content_root"
du -sh "$fixture_dir" | awk '{print "make-upgrade-fixture: fixture size " $1}'
echo "make-upgrade-fixture: done. Commit tests/upgrade-fixtures/$label/ (and run the upgrade tests: dotnet test --filter UpgradeFixture)."
