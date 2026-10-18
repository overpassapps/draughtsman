# Diagrams in git: reviewing a diagram change in a pull request

Draughtsman keeps a diagram as a typed graph and can write it as plain text, the [DSL](spec/dsl.md), one line per node or
edge. Keep those `.dsl` files in your own git repository and a pull request that touches one can say what changed in
sentences, instead of a wall of coordinates:

```
### Diagram changes: `Order service`

- Edge `Orders API` to `Postgres` relabelled from `read/write` to `reads`
- 1 layout-only change
```

Two verbs of the `draughtsman` program do this. They read the files you give them and nothing else: no data folder, no
settings, no database, no network. **Draughtsman never runs git and never holds a git credential**; your repository, your
CI and your forge do all of that. (A server-side "export every diagram into a folder" verb is a possible later step and is
not built.)

## What is verified and what is not

| | Status |
| --- | --- |
| `draughtsman diff` and `draughtsman check`: every example and exit code on this page | Run, and covered by tests (`DiagramFileCommandTests`, `SemanticDiffTests`, `DocumentDiffTests`). |
| The `diagram-review.sh` script below, against a real git repository (git 2.50.1, macOS) with a branch that adds, changes and removes diagrams, finds the base through the merge base, and fails on an error finding and on an unparsable file | Run. |
| `export-if-changed.sh` below | Run, same machine. |
| The `.gitattributes` `diff=` driver hiding the header lines in `git diff` | Run for the patch output only (same git); `git diff --stat` still counts the line, and it never affects a merge. |
| **The GitHub Actions workflow** | **Not yet run on a real runner.** |
| **The GitLab CI job** | **Not yet run on a real runner.** |
| Anything on Windows (the runner, `eol=lf`, `grep` for the driver) | Not run. |

## Getting the diagrams into the repository

Today this is a step you do, with what already exists:

- An agent or script holding an API token calls the MCP tool `read_document` with `format` `dsl`
  ([`api.md`](api.md#post-mcp)). A **read** token is enough, and one is the right size for a job that only exports.
- An administrator can download every diagram as `documents/<id>.dsl` inside the zip from `GET /api/admin/export`
  ([`api.md`](api.md#admin-export)). That is a one-off by hand: the endpoint wants a signed-in admin's browser session, not a token.

Commit the text as it comes. It is canonical: reading a file and printing it again gives the same bytes, so an unchanged
diagram is an unchanged file.

## `draughtsman diff`

```
draughtsman diff <before.dsl> <after.dsl> [--format markdown|json] [--ignore-layout]
```

Parses both files and describes the change. Elements are matched by id, but the sentences use **labels**, never ids, so a
reviewer reads "Edge `Orders API` to `Postgres`" and not `e_2`. Every label and value is printed as inline code on one line
and cut at 120 characters: a label is text someone typed into a diagram, and this output goes into a pull request, so it must
not be able to mention a user or break the page.

| Exit | Meaning |
| --- | --- |
| `0` | No semantic change. |
| `1` | The diagram changed. |
| `2` | A file could not be read or parsed (the message names the file, line and column), or the command line is wrong. |

What is reported, one line each:

- **Document**: title, diagram type, schema version, document id, and each theme override by token (set, changed, removed).
- **Nodes and edges**: added, removed, relabelled, changed kind or shape, moved into or out of a container, an edge
  reconnected, routing mode changed.
- **Props and style, one line per key** (`prop `owner` changed from `platform` to `data``, not "props changed").
- **Position** (a node moved or resized, an edge rerouted, a node's declared ports changed): reported as it happened by default.
- **Order**: in a `sequence` diagram the order of the messages is time, so moving one is a change; in any other type it is only
  the drawing order and counts as layout.
- **The fly-through**, when its path differs.

### `--ignore-layout`

Position, size, rotation, waypoint and port-only changes (and the drawing order outside sequence diagrams) are collapsed into
one line: `12 layout-only changes`, counting each element once. If layout is all that moved, the output says
`No semantic changes; 12 layout-only changes.` and the exit code is `0`: you asked to ignore layout, so it does not fail a
check. A change made alongside the layout is still listed in full. One port change is never layout: an edge that now names a
different table column (`col:<name>`, see [`er-entities.md`](er-entities.md)) says something about the data.

### The lines every save rewrites

`meta created= updated= by=` and the `provenance` lines change on every save, whether or not the diagram did. `diff` lists
them **apart**, under "Not counted as changes", and they never decide the exit code or appear as an element change, so a save
that changed nothing is not a change:

```
### Diagram changes: `Order service`

No changes.

Not counted as changes (every save rewrites these):

- Last saved time changed from `2026-09-20T22:05:00.000Z` to `2026-10-01T10:00:00.000Z`
```

### `--format json`

```json
{"title":"Order service","ignoreLayout":true,"changes":[{"category":"edge","elementId":"e_2","text":"Edge `Orders API` to `Postgres` relabelled from `read/write` to `reads`","layout":false}],"layoutOnlyChanges":1,"housekeeping":[…],"hasChanges":true}
```

`category` is `document`, `node`, `edge`, `order` or `flythrough`. `elementId` is there for a script (the sentence never
carries it). Without `--ignore-layout`, layout entries are in `changes` with `"layout": true` and `layoutOnlyChanges` is `0`.

## `draughtsman check`

```
draughtsman check <file.dsl> [--format text|json]
```

Reports the parse warnings (an unknown kind, an edge to a node that is not declared) and the deterministic critique findings
(the rules `critique_document` runs, listed in [`api.md`](api.md)):

```
order.dsl:23:24: warning: edge 'e_9' refers to node 'n_missing', which is not declared
order.dsl: error [dangling-edge]: Edge 'e_9' references target node 'n_missing', which does not exist. (edges e_9)
order.dsl: 1 error, 1 warning
```

Exit `0` when there is no error-severity finding (warnings are printed and do not fail), `1` for at least one error, `2` when
the file could not be read or parsed. The JSON form has `errors`, `warnings`, `parseWarnings` and `findings`.

## A script that reviews a branch

Both recipes below call this script, which you keep in your repository (for example `tools/diagram-review.sh`). It prints the
Markdown for every `.dsl` file the branch changed.

```bash
#!/usr/bin/env bash
# diagram-review.sh <base-ref>
# Prints a Markdown review of every .dsl file this branch changed against <base-ref> (for example origin/main).
# Exit 0: fine. Exit 1: a diagram has an error-severity finding. Exit 2: a file could not be read or parsed.
set -u
base_ref="$1"
draughtsman="${DRAUGHTSMAN:-draughtsman}"
status=0

# The base side is the merge base, not the tip of the base branch: against the tip, anything that landed on main since
# this branch started would show up as a change this branch made.
base="$(git merge-base "$base_ref" HEAD)" || exit 2
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

changes="$(git diff --name-status --no-renames "$base" HEAD -- '*.dsl')"
if [ -z "$changes" ]; then
  echo "No diagram changes."
  exit 0
fi

while IFS=$'\t' read -r kind path; do
  case "$kind" in
    D)
      printf '### Removed diagram: `%s`\n\n' "$path"
      continue
      ;;
    A)
      printf '### New diagram: `%s`\n\n' "$path"
      ;;
    M)
      git show "$base:$path" > "$work/base.dsl" || { status=2; continue; }
      "$draughtsman" diff "$work/base.dsl" "$path" --ignore-layout
      rc=$?
      # 0 and 1 are "no change" and "changed"; only 2 (unreadable or unparsable) is a failure here.
      if [ "$rc" -ge 2 ]; then status=2; fi
      echo
      ;;
  esac
  report="$("$draughtsman" check "$path")"
  rc=$?
  if [ "$rc" -eq 1 ] && [ "$status" -eq 0 ]; then status=1; fi
  if [ "$rc" -ge 2 ] && [ "$status" -lt 2 ]; then status=2; fi
  printf 'Checks for `%s`:\n\n```\n%s\n```\n\n' "$path" "$report"
done <<< "$changes"

exit "$status"
```

**The base side is the merge base.** `git show origin/main:path` is the base file only until `main` moves on; after that it
shows other people's later changes as if this branch had undone them. The script resolves `origin/main` to
`git merge-base origin/main HEAD` first, so `git show <merge-base>:path` is the file the branch started from. Your CI must
fetch enough history for that: `fetch-depth: 0` below, `GIT_DEPTH: "0"` for GitLab.

**New files** have no base, so they get `check` only; **removed files** are named; **renames** are shown as a removal and an
addition (`--no-renames`).

**The program itself.** Get the `draughtsman` binary onto the runner the way you get any internal tool: from the release
archive you already hold (`draughtsman-<version>-linux-x64.tar.gz`, see [`install.md`](install.md)), kept in an internal
artifact store or a tools image. The recipes name it through `DRAUGHTSMAN`. `diff` and `check` need nothing else installed
and open no connection, so they run on a runner with no internet access.

## GitHub Actions

**Not yet run on a real runner.** The script it calls has been run; the workflow around it has not.

```yaml
name: Diagram review
on:
  pull_request:
    paths: ["**/*.dsl"]

permissions:
  contents: read

jobs:
  diagrams:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
        with:
          fetch-depth: 0   # the merge base needs the history

      - name: Install draughtsman
        run: |
          # Replace with however your organisation fetches its release archive.
          tar -xzf "$ARCHIVE" -C "$RUNNER_TEMP"
          echo "DRAUGHTSMAN=$(echo "$RUNNER_TEMP"/draughtsman-*/draughtsman)" >> "$GITHUB_ENV"
        env:
          ARCHIVE: tools/draughtsman-0.1.0-linux-x64.tar.gz

      - name: Review the diagrams
        env:
          BASE_REF: origin/${{ github.base_ref }}
        run: |
          set -o pipefail
          bash tools/diagram-review.sh "$BASE_REF" | tee diagram-review.md >> "$GITHUB_STEP_SUMMARY"
```

The Markdown lands on the run's summary page. The job fails when a diagram has an error finding or a file does not parse;
`set -o pipefail` is what carries the script's exit code through `tee`.

To post it as a comment instead, give the job `pull-requests: write` and add a step after the one above, with
`if: always()`:

```yaml
      - name: Comment on the pull request
        if: always()
        env:
          GH_TOKEN: ${{ github.token }}
        run: gh pr comment "${{ github.event.pull_request.number }}" --body-file diagram-review.md
```

A pull request from a fork gets a read-only token, so the comment fails there; the step summary does not.

## GitLab CI

**Not yet run on a real runner.** GitLab has no job summary, so the report is the job log and an artifact; the optional last
line posts it as a merge request note.

```yaml
diagram-review:
  stage: test
  rules:
    - if: $CI_PIPELINE_SOURCE == "merge_request_event"
      changes:
        - "**/*.dsl"
  variables:
    GIT_DEPTH: "0"   # the merge base needs the history
  before_script:
    # Replace with however your organisation fetches its release archive.
    - mkdir -p "$CI_PROJECT_DIR/.draughtsman"
    - tar -xzf tools/draughtsman-0.1.0-linux-x64.tar.gz -C "$CI_PROJECT_DIR/.draughtsman"
    - export DRAUGHTSMAN="$(echo "$CI_PROJECT_DIR"/.draughtsman/draughtsman-*/draughtsman)"
    - git fetch origin "$CI_MERGE_REQUEST_TARGET_BRANCH_NAME"
  script:
    - set -o pipefail
    - bash tools/diagram-review.sh "origin/$CI_MERGE_REQUEST_TARGET_BRANCH_NAME" | tee diagram-review.md
  after_script:
    # Optional: needs a project access token with the api scope in GITLAB_NOTE_TOKEN.
    - >
      curl --fail --silent --header "PRIVATE-TOKEN: $GITLAB_NOTE_TOKEN"
      --data-urlencode "body@diagram-review.md"
      "$CI_API_V4_URL/projects/$CI_PROJECT_ID/merge_requests/$CI_MERGE_REQUEST_IID/notes"
  artifacts:
    when: always
    paths:
      - diagram-review.md
```

Drop the `after_script` block for the log-and-artifact version.

## The header lines in git itself

The saved time and the provenance lines are rewritten on every save, so they change in `git diff` and, when two branches both
edit one diagram, **they will conflict**. Draughtsman does not change the file format to avoid that. Three things that help:

1. **Do not write a file when the diagram did not change.** If your export step replaces the committed file with
   today's export, a save that changed nothing becomes a one-line commit. This script keeps the committed file unless
   `diff` says the diagram differs (layout counts, since a moved box is a real change to the file):

   ```bash
   #!/usr/bin/env bash
   # export-if-changed.sh <new.dsl> <committed.dsl>
   draughtsman="${DRAUGHTSMAN:-draughtsman}"
   new="$1"; committed="$2"
   if [ -f "$committed" ]; then
     "$draughtsman" diff "$committed" "$new" > /dev/null
     rc=$?
     if [ "$rc" -eq 0 ]; then exit 0; fi                                     # same diagram: keep the committed file
     if [ "$rc" -ge 2 ]; then echo "cannot compare $committed with $new" >&2; exit "$rc"; fi
   fi
   cp "$new" "$committed"
   ```

2. **Hide the lines when you read a diff.** In `.gitattributes`, committed with the repository:

   ```
   *.dsl text eol=lf diff=draughtsman
   ```

   and, once per clone (git does not let a repository ship this setting):

   ```
   git config diff.draughtsman.textconv "grep -v -e '^meta ' -e '^provenance '"
   ```

   `git diff` then leaves the `meta` and `provenance` lines out, so a save-only change shows nothing. It changes
   what you see, never what is stored or merged, and `git diff --stat` still counts the line. `text eol=lf` keeps the file the
   LF text `Dsl.Print` writes (the parser also reads CRLF, so a checkout that rewrote it would still diff correctly, but
   it would not be byte-identical to a re-export; not tested on Windows).
3. **When a merge does conflict on those lines, take either side.** They are bookkeeping, not part of the diagram. Resolve the
   `meta` line to the newer `updated=`, and merge the `provenance` lines by keeping both. A conflict on a node or edge line
   is a real one: two people changed the same element. A merge driver that does this by id is feasible, because every
   element is its own line, but it is not built.

A few behaviours of the text form can make a hand-edited file read back slightly differently from what you wrote (a dotted id
is ambiguous with a port, and empty lists are not written); [`spec/dsl.md`](spec/dsl.md) lists them. A file exported by
Draughtsman does not have the problem.
